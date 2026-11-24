using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Tests.SaveHandlers;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The three places a handler is declared in (with the context, with a module, with the host), the order between
/// them, open generic handlers, and where a handler is created from: the services of the saving context, which a
/// context from a pool, from a factory or from <c>new</c> must be given. And what a handler that takes nothing finds
/// in its rows' <c>Services</c>.
/// </summary>
public class DeclaringHandlersTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SqliteConnection connection = new("Data Source=:memory:");

    public DeclaringHandlersTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    // ── The model ───────────────────────────────────────────────────────────────

    public interface ILabelled
    {
        string? Label { get; }
    }

    public class Parcel : ShiftEntity<Parcel>, ILabelled
    {
        public string? Label { get; set; }
    }

    public class Express : Parcel;

    public class Stamp : ShiftEntity<Stamp>, ILabelled
    {
        public string? Label { get; set; }
    }

    /// <summary>A row that no handler has.</summary>
    public class Receipt : ShiftEntity<Receipt>
    {
        public string? Label { get; set; }
    }

    /// <summary>An entity type that a module contributes to the context of the host it is registered in.</summary>
    public class Voucher : ShiftEntity<Voucher>, ILabelled
    {
        public string? Label { get; set; }
    }

    /// <summary>Writes one line for each of the three stages it is in, with its own name, so that the order shows.</summary>
    public abstract class Named(SaveLog log) : IBeforeSaveHandler<Parcel>, IAfterCommitHandler<Parcel>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Parcel> rows, CancellationToken cancellationToken)
        {
            log.Add($"{GetType().Name} before save");
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCommitAsync(SavedRows<Parcel> rows, CancellationToken cancellationToken)
        {
            log.Add($"{GetType().Name} after commit");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class OfTheContext(SaveLog log) : Named(log);
    public sealed class OfTheContextSecond(SaveLog log) : Named(log);
    public sealed class OfTheModule(SaveLog log) : Named(log);
    public sealed class OfTheHost(SaveLog log) : Named(log);
    public sealed class OfTheHostEarly(SaveLog log) : Named(log);
    public sealed class DeclaredTwice(SaveLog log) : Named(log);

    /// <summary>A handler of the module's own entity type.</summary>
    public sealed class VoucherHandler(SaveLog log) : IAfterCommitHandler<Voucher>
    {
        public ValueTask AfterCommitAsync(SavedRows<Voucher> rows, CancellationToken cancellationToken)
        {
            log.Add("the module's handler: " + string.Join(", ", rows.Select(row => row.Entity.Label)));
            return ValueTask.CompletedTask;
        }
    }

    public sealed class VoucherModule : IModelBuildingContributor
    {
        public void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<Voucher>();

        public void ConfigureSaveHandlers(SaveHandlerSet handlers) =>
            handlers.Add<OfTheModule>().Add<VoucherHandler>().Add<DeclaredTwice>(order: 9);
    }

    /// <summary>A context that declares the handlers that belong to its model.</summary>
    public class ParcelDb(DbContextOptions options) : ShiftDbContext(options)
    {
        public static int Configured;

        public DbSet<Parcel> Parcels => Set<Parcel>();
        public DbSet<Express> ExpressParcels => Set<Express>();
        public DbSet<Stamp> Stamps => Set<Stamp>();
        public DbSet<Receipt> Receipts => Set<Receipt>();

        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers)
        {
            base.ConfigureSaveHandlers(handlers);
            Interlocked.Increment(ref Configured);
            handlers.Add<OfTheContext>().Add<OfTheContextSecond>();
        }
    }

    /// <summary>The same context in a host that has the module.</summary>
    public sealed class ModularDb(DbContextOptions<ModularDb> options) : ParcelDb(options)
    {
        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers)
        {
            base.ConfigureSaveHandlers(handlers);
            handlers.Add<DeclaredTwice>(order: -9);
        }
    }

    public sealed class PlainDb(DbContextOptions<PlainDb> options) : ParcelDb(options);

    private ServiceProvider Host<TDb>(Action<IServiceCollection>? services = null, bool validateScopes = true) where TDb : ParcelDb
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<SaveLog>();
        collection.AddDbContext<TDb>(options => options.UseSqlite(connection));
        services?.Invoke(collection);

        var provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TDb>().Database.EnsureCreated();

        return provider;
    }

    // ── With the context, with a module, with the host ──────────────────────────

    [Fact]
    public async Task A_handler_declared_with_the_context_runs_in_a_host_that_registers_nothing()
    {
        // The host registers its context and nothing else. The handlers are not registered in its services: the
        // context creates them, with their constructors injected.
        await using var provider = Host<PlainDb>();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlainDb>();
            db.Add(new Parcel { Label = "a" });
            await db.SaveChangesAsync(Ct);

            Assert.Null(scope.ServiceProvider.GetService<OfTheContext>());
        }

        Assert.Equal(
        [
            "OfTheContext before save", "OfTheContextSecond before save",
            "OfTheContext after commit", "OfTheContextSecond after commit",
        ], provider.GetRequiredService<SaveLog>().Lines);
    }

    [Fact]
    public async Task A_handler_declared_by_a_modules_model_contributor_runs_in_a_host_that_only_registers_the_module()
    {
        await using var provider = Host<ModularDb>(services => services.AddSingleton<IModelBuildingContributor, VoucherModule>());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModularDb>();
            db.Add(new Voucher { Label = "ten percent" });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(["the module's handler: ten percent"], provider.GetRequiredService<SaveLog>().Lines);
    }

    [Fact]
    public async Task Handlers_run_by_their_order_and_with_the_same_order_the_contexts_first_then_the_modules_then_the_hosts()
    {
        await using var provider = Host<ModularDb>(services => services
            .AddSaveHandler<OfTheHost>()
            .AddSaveHandler<OfTheHostEarly>(order: -1)
            .AddSaveHandler<DeclaredTwice>(order: 5)
            .AddSingleton<IModelBuildingContributor, VoucherModule>());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModularDb>();
            db.Add(new Parcel { Label = "a" });
            await db.SaveChangesAsync(Ct);
        }

        // The class that three places declare is one handler, and the first declaration counts: the context's, with
        // order -9. Then order -1, then the handlers with order 0: the context's, the module's, the host's.
        string[] expected = ["DeclaredTwice", "OfTheHostEarly", "OfTheContext", "OfTheContextSecond", "OfTheModule", "OfTheHost"];

        Assert.Equal(
            expected.Select(name => name + " before save").Concat(expected.Select(name => name + " after commit")),
            provider.GetRequiredService<SaveLog>().Lines);
    }

    [Fact]
    public async Task ConfigureSaveHandlers_is_called_once_for_a_context_type_in_a_host_however_many_contexts_save()
    {
        await using var provider = Host<PlainDb>();
        var before = ParcelDb.Configured;

        for (var i = 0; i < 3; i++)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlainDb>();
            db.Add(new Parcel { Label = "parcel " + i });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, ParcelDb.Configured - before);
    }

    /// <summary>A context that declares a handler only where its database is not SQL Server.</summary>
    public sealed class ProviderDb(DbContextOptions<ProviderDb> options) : ShiftDbContext(options)
    {
        public static readonly List<string?> ConfiguredOn = [];

        public DbSet<Parcel> Parcels => Set<Parcel>();

        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers)
        {
            base.ConfigureSaveHandlers(handlers);

            lock (ConfiguredOn)
                ConfiguredOn.Add(Database.ProviderName);

            if (!Database.IsSqlServer())
                handlers.Add<OffSqlServer>();
        }
    }

    public sealed class OffSqlServer : IBeforeSaveHandler<Parcel>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Parcel> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public void ConfigureSaveHandlers_is_called_once_for_each_model_so_a_context_may_declare_a_handler_for_one_database_only()
    {
        // EF builds one model for each database provider, and the handlers are worked out once for each model in a host.
        // Neither context connects: only their models and their handlers are read.
        using var onSqlite = new ProviderDb(new DbContextOptionsBuilder<ProviderDb>().UseSqlite(connection).Options);
        using var onSqlServer = new ProviderDb(new DbContextOptionsBuilder<ProviderDb>().UseSqlServer("Server=unused.invalid;Database=unused").Options);
        using var onSqliteAgain = new ProviderDb(new DbContextOptionsBuilder<ProviderDb>().UseSqlite(connection).Options);

        var sqlite = onSqlite.SaveHandlers;

        Assert.NotNull(sqlite);
        Assert.Contains(sqlite.For(typeof(Parcel)).All, call => call.HandlerType == typeof(OffSqlServer));
        Assert.Same(sqlite, onSqliteAgain.SaveHandlers);

        // On SQL Server the context declares nothing, so it has no handlers at all: a save there pays for none.
        Assert.Null(onSqlServer.SaveHandlers);

        Assert.Equal(["Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.EntityFrameworkCore.SqlServer"], ProviderDb.ConfiguredOn);
    }

    // ── Open generic handlers ───────────────────────────────────────────────────

    public sealed class Journal<TEntity>(SaveLog log) : IAfterCommitHandler<TEntity> where TEntity : ShiftEntity<TEntity>, ILabelled
    {
        public ValueTask AfterCommitAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken)
        {
            log.Add($"journal of {typeof(TEntity).Name}: {string.Join(", ", rows.Select(row => $"{row.Change} {row.Entity.Label}"))}");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class JournalDb(DbContextOptions<JournalDb> options) : ParcelDb(options)
    {
        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers) => handlers.Add(typeof(Journal<>));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_open_generic_handler_is_closed_over_every_entity_type_of_the_model_that_fits_its_constraints(bool withTheContext)
    {
        // Declared with the context, or added by the host for a context that declares none.
        await using var provider = withTheContext
            ? Host<JournalDb>()
            : Host<BareOfHandlersDb>(services => services.AddSaveHandler(typeof(Journal<>)));

        await using (var scope = provider.CreateAsyncScope())
        {
            ParcelDb db = withTheContext
                ? scope.ServiceProvider.GetRequiredService<JournalDb>()
                : scope.ServiceProvider.GetRequiredService<BareOfHandlersDb>();

            db.Add(new Parcel { Label = "parcel" });
            db.Add(new Stamp { Label = "stamp" });
            db.Add(new Receipt { Label = "does not fit: a receipt has no label interface" });
            await db.SaveChangesAsync(Ct);
        }

        // One handler for each entity type that fits: Parcel and Stamp, and not Receipt. Express derives from Parcel,
        // so it is not a ShiftEntity of its own type and does not fit either.
        Assert.Equal(["journal of Parcel: Added parcel", "journal of Stamp: Added stamp"],
            provider.GetRequiredService<SaveLog>().Lines.Order(StringComparer.Ordinal));
    }

    public sealed class BareOfHandlersDb(DbContextOptions<BareOfHandlersDb> options) : ParcelDb(options)
    {
        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers) { }
    }

    // ── What cannot be declared ─────────────────────────────────────────────────

    public sealed class ImplementsNoStage;

    public abstract class CannotBeCreated : IAfterCommitHandler<Parcel>
    {
        public abstract ValueTask AfterCommitAsync(SavedRows<Parcel> rows, CancellationToken cancellationToken);
    }

    public sealed class TwoTypeParameters<TFirst, TSecond> : IAfterCommitHandler<TFirst> where TFirst : class
    {
        public ValueTask AfterCommitAsync(SavedRows<TFirst> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Guard]
    public sealed class GuardAfterTheCommit : IBeforeSaveHandler<Parcel>, IAfterCommitHandler<Parcel>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Parcel> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AfterCommitAsync(SavedRows<Parcel> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(typeof(ImplementsNoStage), "cannot be declared as a save handler")]
    [InlineData(typeof(CannotBeCreated), "cannot be declared as a save handler")]
    [InlineData(typeof(TwoTypeParameters<,>), "cannot be declared as a save handler")]
    [InlineData(typeof(IAfterCommitHandler<Parcel>), "cannot be declared as a save handler")]
    [InlineData(typeof(GuardAfterTheCommit), "is marked [Guard] and implements IAfterCommitHandler<T>")]
    public void A_class_that_cannot_be_a_handler_is_refused_where_it_is_declared(Type handlerType, string message)
    {
        // With the host, and with a context or a module: the same rules.
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSaveHandler(handlerType)).Message);
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => new SaveHandlerSet().Add(handlerType)).Message);
    }

    // ── No services, and the root provider's ────────────────────────────────────

    [Fact]
    public async Task A_context_with_a_declared_handler_and_no_services_refuses_to_save_a_row_that_has_it()
    {
        await using var provider = Host<PlainDb>();
        var log = provider.GetRequiredService<SaveLog>();

        // Built with new, as a tool builds it: no service provider at all.
        await using var db = new PlainDb(new DbContextOptionsBuilder<PlainDb>().UseSqlite(connection).Options);

        var parcel = new Parcel { Label = "a" };
        db.Add(parcel);

        // The handler's constructor takes a service, so it cannot be created, and skipping it would be silent.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        Assert.Contains("PlainDb is saving a Parcel row, which has the save handler OfTheContext (before save, Parcel)", refused.Message);
        Assert.Contains("no services to create the handler from", refused.Message);
        Assert.Contains("AttachServices(scope.ServiceProvider)", refused.Message);
        Assert.Contains("SuppressSaveHandler<OfTheContext>()", refused.Message);
        Assert.Equal(0, await db.Parcels.AsNoTracking().CountAsync(Ct));

        // A row that no handler has is saved as always.
        db.Entry(parcel).State = EntityState.Detached;
        db.Add(new Receipt { Label = "no handler" });
        await db.SaveChangesAsync(Ct);

        // The save names the handlers it means to skip.
        db.Add(parcel);
        using (db.SuppressSaveHandler<OfTheContext>())
        using (db.SuppressSaveHandler<OfTheContextSecond>())
            await db.SaveChangesAsync(Ct);

        Assert.Empty(log.Lines);

        // Or the context is given the services of the scope the work runs in.
        await using var scope = provider.CreateAsyncScope();
        db.AttachServices(scope.ServiceProvider);
        db.Add(new Parcel { Label = "b" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(4, log.Lines.Count);
        Assert.Equal(2, await db.Parcels.AsNoTracking().CountAsync(Ct));
    }

    /// <summary>A handler whose constructor takes nothing: it needs no scope to be created in.</summary>
    public sealed class NeedsNothing : IBeforeSaveHandler<Stamp>
    {
        public ValueTask BeforeSaveAsync(PendingRows<Stamp> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
                row.Entity.Label += ", checked";

            return ValueTask.CompletedTask;
        }
    }

    public sealed class NeedsNothingDb(DbContextOptions<NeedsNothingDb> options) : ParcelDb(options)
    {
        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers) => handlers.Add<NeedsNothing>();
    }

    [Fact]
    public async Task A_declared_handler_whose_constructor_takes_nothing_runs_on_a_context_built_without_services()
    {
        // A handler that keeps data right is there for a tool, a test and a seeder too, with nothing to attach.
        await using var db = new NeedsNothingDb(new DbContextOptionsBuilder<NeedsNothingDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(Ct);

        db.Add(new Stamp { Label = "a" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal("a, checked", await db.Stamps.AsNoTracking().Select(x => x.Label).SingleAsync(Ct));
    }

    [Fact]
    public async Task A_handler_whose_constructor_takes_nothing_runs_on_a_pooled_context_and_is_not_taken_from_the_root_provider()
    {
        await using var provider = RootHost(From.APooledFactory, validateScopes: true, services => services.AddSaveHandler<NeedsNothing>());

        await using var db = await provider.GetRequiredService<IDbContextFactory<PooledDb>>().CreateDbContextAsync(Ct);
        db.Add(new Stamp { Label = "a" });

        // The host registered the class as a scoped service. The context creates it itself, so nothing is resolved
        // from the root provider, where scope validation would refuse it and production would keep it.
        await db.SaveChangesAsync(Ct);

        Assert.Equal("a, checked", await db.Stamps.AsNoTracking().Select(x => x.Label).SingleAsync(Ct));
    }

    public sealed class ScopedThing
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    /// <summary>A handler that holds a service of its scope, as one that reads the current user does.</summary>
    public sealed class UsesItsScope(ScopedThing thing, SaveLog log) : IAfterCommitHandler<Stamp>
    {
        public ValueTask AfterCommitAsync(SavedRows<Stamp> rows, CancellationToken cancellationToken)
        {
            log.Add("handled with the scope's " + thing.Id);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class SingletonHandler(SaveLog log) : IAfterCommitHandler<Stamp>
    {
        public ValueTask AfterCommitAsync(SavedRows<Stamp> rows, CancellationToken cancellationToken)
        {
            log.Add("handled by the singleton");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class PooledDb(DbContextOptions<PooledDb> options) : ParcelDb(options)
    {
        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers) { }
    }

    public enum From { APool, APooledFactory, AFactory }

    private ServiceProvider RootHost(From from, bool validateScopes, Action<IServiceCollection> services)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<SaveLog>();
        collection.AddScoped<ScopedThing>();

        switch (from)
        {
            case From.APool:
                collection.AddDbContextPool<PooledDb>(options => options.UseSqlite(connection));
                break;
            case From.APooledFactory:
                collection.AddPooledDbContextFactory<PooledDb>(options => options.UseSqlite(connection));
                break;
            default:
                collection.AddDbContextFactory<PooledDb>(options => options.UseSqlite(connection));
                break;
        }

        services(collection);

        var provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });

        using var setup = new PooledDb(new DbContextOptionsBuilder<PooledDb>().UseSqlite(connection).Options);
        setup.Database.EnsureCreated();

        return provider;
    }

    private static async Task<PooledDb> LeaseAsync(From from, IServiceProvider scope) => from == From.APool
        ? scope.GetRequiredService<PooledDb>()
        : await scope.GetRequiredService<IDbContextFactory<PooledDb>>().CreateDbContextAsync(Ct);

    [Theory]
    [InlineData(From.APool, false)]
    [InlineData(From.APool, true)]
    [InlineData(From.APooledFactory, false)]
    [InlineData(From.APooledFactory, true)]
    [InlineData(From.AFactory, false)]
    [InlineData(From.AFactory, true)]
    public async Task A_scoped_handler_on_a_context_whose_services_are_the_root_provider_is_refused_until_services_are_attached(
        From from, bool validateScopes)
    {
        // In production nothing validates scopes: the handler would be created from the root provider and kept, with
        // the scoped services it holds, for the life of the process. The context refuses in every environment.
        await using var provider = RootHost(from, validateScopes, services => services.AddSaveHandler<UsesItsScope>());
        var log = provider.GetRequiredService<SaveLog>();

        await using var scope = provider.CreateAsyncScope();
        var db = await LeaseAsync(from, scope.ServiceProvider);

        try
        {
            var stamp = new Stamp { Label = "a" };
            db.Add(stamp);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("PooledDb is saving a Stamp row, which has the save handler UsesItsScope (after commit, Stamp)", refused.Message);
            Assert.Contains("the context's services are the root provider", refused.Message);
            Assert.Contains("AttachServices(scope.ServiceProvider)", refused.Message);
            Assert.Equal(0, await db.Stamps.AsNoTracking().CountAsync(Ct));
            Assert.Empty(log.Lines);

            // With the scope's services the handler is the scope's.
            db.AttachServices(scope.ServiceProvider);
            await db.SaveChangesAsync(Ct);

            Assert.Equal(["handled with the scope's " + scope.ServiceProvider.GetRequiredService<ScopedThing>().Id], log.Lines);
        }
        finally
        {
            if (from != From.APool)
                await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_singleton_handler_is_created_from_the_root_provider()
    {
        await using var provider = RootHost(From.APooledFactory, validateScopes: true,
            services => services.AddSingleton<SingletonHandler>().AddSaveHandler<SingletonHandler>());

        await using var db = await provider.GetRequiredService<IDbContextFactory<PooledDb>>().CreateDbContextAsync(Ct);
        db.Add(new Stamp { Label = "a" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(["handled by the singleton"], provider.GetRequiredService<SaveLog>().Lines);
    }

    [Fact]
    public async Task A_pooled_context_forgets_the_attached_services_and_the_switches_of_its_last_lease()
    {
        await using var provider = RootHost(From.APooledFactory, validateScopes: true, services => services.AddSaveHandler<UsesItsScope>());
        var factory = provider.GetRequiredService<IDbContextFactory<PooledDb>>();
        var log = provider.GetRequiredService<SaveLog>();

        PooledDb first;

        await using (var scope = provider.CreateAsyncScope())
        await using (first = await factory.CreateDbContextAsync(Ct))
        {
            first.AttachServices(scope.ServiceProvider);
            first.Add(new Stamp { Label = "a" });
            await first.SaveChangesAsync(Ct);

            // Left on when the context goes back to the pool.
            _ = first.SuppressAllSaveHandlers();
            _ = first.SuppressAfterCommit();
        }

        Assert.Single(log.Lines);

        // The same instance, leased again: neither the services of the scope that is gone, nor the switches.
        await using var second = await factory.CreateDbContextAsync(Ct);
        Assert.Same(first, second);

        second.Add(new Stamp { Label = "b" });
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => second.SaveChangesAsync(Ct));
        Assert.Contains("the context's services are the root provider", refused.Message);
    }

    // ── The services of a save, as a handler finds them in its rows ─────────────

    /// <summary>A handler that takes nothing, and asks its rows for what it needs.</summary>
    public sealed class AsksItsRows : IBeforeSaveHandler<Stamp>, IAfterCommitHandler<Stamp>
    {
        public static readonly AsyncLocal<List<(string Stage, IServiceProvider Services, ScopedThing? Thing)>?> Seen = new();

        public ValueTask BeforeSaveAsync(PendingRows<Stamp> rows, CancellationToken cancellationToken) => Note("before save", rows);

        public ValueTask AfterCommitAsync(SavedRows<Stamp> rows, CancellationToken cancellationToken) => Note("after commit", rows);

        private static ValueTask Note(string stage, RowSet<Stamp> rows)
        {
            Seen.Value!.Add((stage, rows.Services, rows.Services.GetService<ScopedThing>()));
            return ValueTask.CompletedTask;
        }
    }

    public sealed class AsksItsRowsDb(DbContextOptions<AsksItsRowsDb> options) : ParcelDb(options)
    {
        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers) => handlers.Add<AsksItsRows>();
    }

    [Fact]
    public async Task The_rows_of_a_handler_give_the_services_of_the_scope_the_context_came_from()
    {
        await using var provider = Host<AsksItsRowsDb>(services => services.AddScoped<ScopedThing>());
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AsksItsRowsDb>();
        var seen = AsksItsRows.Seen.Value = [];

        db.Add(new Stamp { Label = "a" });
        await db.SaveChangesAsync(Ct);

        // In every stage: the scope itself, so a scoped service is the one the rest of the request has.
        Assert.Equal(["before save", "after commit"], seen.Select(x => x.Stage));
        Assert.All(seen, x => Assert.Same(scope.ServiceProvider, x.Services));
        Assert.All(seen, x => Assert.Same(scope.ServiceProvider.GetRequiredService<ScopedThing>(), x.Thing));
    }

    [Fact]
    public async Task The_rows_of_a_handler_on_a_context_without_services_give_a_provider_with_nothing_registered_until_services_are_attached()
    {
        await using var provider = Host<AsksItsRowsDb>(services => services.AddScoped<ScopedThing>());

        // Built by hand, as a tool, a seeder and a test fixture build it.
        await using var db = new AsksItsRowsDb(new DbContextOptionsBuilder<AsksItsRowsDb>().UseSqlite(connection).Options);
        var seen = AsksItsRows.Seen.Value = [];

        db.Add(new Stamp { Label = "a" });
        await db.SaveChangesAsync(Ct);

        // Never null. The handler asks, and gets no answer: there is no host to give one.
        Assert.Equal(2, seen.Count);
        Assert.All(seen, x => Assert.Null(x.Thing));

        var nothing = seen[0].Services;
        Assert.Same(nothing, seen[1].Services);
        Assert.Null(nothing.GetService<SaveLog>());
        Assert.Empty(nothing.GetServices<SaveLog>());
        Assert.Throws<InvalidOperationException>(() => nothing.GetRequiredService<SaveLog>());

        // Given the services of a scope, the same handler on the same context finds them.
        await using var scope = provider.CreateAsyncScope();
        db.AttachServices(scope.ServiceProvider);
        seen.Clear();

        db.Add(new Stamp { Label = "b" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, seen.Count);
        Assert.All(seen, x => Assert.Same(scope.ServiceProvider, x.Services));
        Assert.All(seen, x => Assert.Same(scope.ServiceProvider.GetRequiredService<ScopedThing>(), x.Thing));
    }

    [Fact]
    public async Task The_rows_of_a_handler_on_a_pooled_context_give_the_root_provider_until_services_are_attached()
    {
        // Scope validation is off, as it is in production: there the root provider hands out a scoped service.
        await using var provider = RootHost(From.APooledFactory, validateScopes: false, services => services.AddSaveHandler<AsksItsRows>());
        var root = provider.GetRequiredService<IServiceProvider>();
        var seen = AsksItsRows.Seen.Value = [];

        await using var db = await provider.GetRequiredService<IDbContextFactory<PooledDb>>().CreateDbContextAsync(Ct);
        db.Add(new Stamp { Label = "a" });
        await db.SaveChangesAsync(Ct);

        // The services of the save are what the context has: the provider its pool belongs to.
        Assert.Equal(2, seen.Count);
        Assert.All(seen, x => Assert.Same(root, x.Services));

        await using var scope = provider.CreateAsyncScope();
        db.AttachServices(scope.ServiceProvider);
        seen.Clear();

        db.Add(new Stamp { Label = "b" });
        await db.SaveChangesAsync(Ct);

        Assert.All(seen, x => Assert.Same(scope.ServiceProvider, x.Services));
        Assert.All(seen, x => Assert.Same(scope.ServiceProvider.GetRequiredService<ScopedThing>(), x.Thing));
    }

    [Fact]
    public async Task A_row_set_built_for_a_test_gives_the_services_of_its_context_and_says_whose_save_it_is()
    {
        await using var provider = Host<AsksItsRowsDb>(services => services.AddScoped<ScopedThing>());
        await using var scope = provider.CreateAsyncScope();
        var row = new ChangedRow<Stamp>(new Stamp { Label = "a" }, RowChange.Added);

        // A context from a scope: the scope.
        var scoped = scope.ServiceProvider.GetRequiredService<AsksItsRowsDb>();
        Assert.Same(scope.ServiceProvider, HandlerRows.Pending(scoped, row).Services);
        Assert.Same(scope.ServiceProvider, HandlerRows.Saved(scoped, row).Services);

        // A context built by hand: nothing registered, and then what it was given.
        await using var bare = new AsksItsRowsDb(new DbContextOptionsBuilder<AsksItsRowsDb>().UseSqlite(connection).Options);
        var pending = HandlerRows.Pending(bare, row);

        Assert.Null(pending.Services.GetService<ScopedThing>());
        Assert.Null(HandlerRows.Saved(bare, row).Services.GetService<ScopedThing>());

        bare.AttachServices(scope.ServiceProvider);
        Assert.Same(scope.ServiceProvider, pending.Services);

        // A set is any save's, unless the test says that it is a repository's.
        Assert.False(HandlerRows.Pending(bare, row).IsRepositorySave);
        Assert.False(HandlerRows.Pending(bare, isRepositorySave: false, row).IsRepositorySave);

        var ofARepository = HandlerRows.Pending(bare, isRepositorySave: true, row, row);
        Assert.True(ofARepository.IsRepositorySave);
        Assert.Equal(2, ofARepository.Count);
        Assert.Same(bare, ofARepository.Db);

        // A handler that takes nothing is tested as it runs: called with the set, it finds its services there.
        var seen = AsksItsRows.Seen.Value = [];
        await new AsksItsRows().BeforeSaveAsync(ofARepository, Ct);

        Assert.Same(scope.ServiceProvider.GetRequiredService<ScopedThing>(), Assert.Single(seen).Thing);
    }

    // ── Whose the handler is ────────────────────────────────────────────────────

    public sealed class Counts
    {
        public int Created;
        public int Disposed;
    }

    public sealed class Disposable : IAfterCommitHandler<Stamp>, IAsyncDisposable
    {
        private readonly Counts counts;

        public Disposable(Counts counts)
        {
            this.counts = counts;
            Interlocked.Increment(ref counts.Created);
        }

        public ValueTask AfterCommitAsync(SavedRows<Stamp> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref counts.Disposed);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class DisposableDb(DbContextOptions<DisposableDb> options) : ParcelDb(options)
    {
        protected override void ConfigureSaveHandlers(SaveHandlerSet handlers) => handlers.Add<Disposable>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_handler_the_context_creates_belongs_to_its_call_and_one_the_host_registered_belongs_to_the_container(bool registered)
    {
        var counts = new Counts();

        await using var provider = Host<DisposableDb>(services =>
        {
            services.AddSingleton(counts);

            if (registered)
                services.AddScoped<Disposable>();
        });

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DisposableDb>();

            db.Add(new Stamp { Label = "a" });
            await db.SaveChangesAsync(Ct);
            db.Add(new Stamp { Label = "b" });
            await db.SaveChangesAsync(Ct);

            // A save with no row of the handler's type creates no handler.
            db.Add(new Receipt { Label = "c" });
            await db.SaveChangesAsync(Ct);

            // The context's own: created for each call and disposed after it. The container's: one for the scope.
            Assert.Equal(registered ? (1, 0) : (2, 2), (counts.Created, counts.Disposed));
        }

        Assert.Equal(registered ? (1, 1) : (2, 2), (counts.Created, counts.Disposed));
    }
}
