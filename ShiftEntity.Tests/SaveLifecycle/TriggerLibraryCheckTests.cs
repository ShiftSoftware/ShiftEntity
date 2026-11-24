using EntityFrameworkCore.Triggered;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The check that stops a host that still uses EntityFrameworkCore.Triggered. The framework no longer references that
/// library and no longer switches it on, so a trigger that is still registered would stop running without a sign.
/// These are the only tests that reference the library, and the test project references it for them alone.
/// </summary>
public class TriggerLibraryCheckTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public TriggerLibraryCheckTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    public sealed class Note : ShiftEntity<Note>
    {
        public string Text { get; set; } = "";
    }

    public class NotesDb(DbContextOptions options) : ShiftDbContext(options)
    {
        public DbSet<Note> Notes => Set<Note>();
    }

    /// <summary>A context of an app that switches the library on itself, in OnConfiguring.</summary>
    public sealed class SwitchedOnInOnConfiguringDb(DbContextOptions<SwitchedOnInOnConfiguringDb> options) : NotesDb(options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.UseTriggers();
        }
    }

    public sealed class RegisteredDb(DbContextOptions<RegisteredDb> options) : NotesDb(options);

    public sealed class ClosedTrigger : IAfterSaveTrigger<Note>
    {
        public Task AfterSave(ITriggerContext<Note> context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class OpenTrigger<TEntity> : IBeforeSaveTrigger<TEntity> where TEntity : class
    {
        public Task BeforeSave(ITriggerContext<TEntity> context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class Converted : IAfterCommitHandler<Note>
    {
        public ValueTask AfterCommitAsync(SavedRows<Note> rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private IHost Host(Action<IServiceCollection> configure) => new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddDbContext<RegisteredDb>(options => options.UseSqlite(connection));
            configure(services);
        })
        .Build();

    private static async Task<InvalidOperationException> DoesNotStartAsync(IHost host)
    {
        using (host)
            return await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.StartAsync(Ct));
    }

    private static void SaysHowToConvert(string message)
    {
        Assert.Contains("IBeforeSaveTrigger<T>) becomes an IBeforeSaveHandler<T>", message);
        Assert.Contains("IAfterCommitHandler<T> when it tells another system", message);
        Assert.Contains("IAfterSaveHandler<T> when it writes something that belongs to the save", message);
        Assert.Contains("remove UseTriggers and the package reference", message);
    }

    // ── A trigger registered in the host's services ─────────────────────────────

    [Fact]
    public async Task A_host_with_a_trigger_registered_in_its_services_does_not_start_and_the_message_names_it()
    {
        var refused = await DoesNotStartAsync(Host(services => services
            .AddSaveHandler<Converted>()
            .AddScoped<IAfterSaveTrigger<Note>, ClosedTrigger>()));

        Assert.Contains("This host still uses EntityFrameworkCore.Triggered", refused.Message);
        Assert.Contains("Registered in its services: ClosedTrigger as IAfterSaveTrigger<Note>.", refused.Message);
        SaysHowToConvert(refused.Message);
    }

    [Fact]
    public async Task A_host_with_an_open_generic_trigger_does_not_start_and_the_message_names_it()
    {
        var refused = await DoesNotStartAsync(Host(services => services
            .AddSaveHandler<Converted>()
            .AddScoped(typeof(IBeforeSaveTrigger<>), typeof(OpenTrigger<>))));

        Assert.Contains("Registered in its services: OpenTrigger<> as IBeforeSaveTrigger<>.", refused.Message);
        SaysHowToConvert(refused.Message);
    }

    [Fact]
    public async Task Every_trigger_and_every_context_found_is_named_in_one_message()
    {
        var refused = await DoesNotStartAsync(Host(services => services
            .AddSaveHandler<Converted>()
            .AddScoped<IAfterSaveTrigger<Note>, ClosedTrigger>()
            .AddScoped(typeof(IBeforeSaveTrigger<>), typeof(OpenTrigger<>))
            .AddDbContext<SwitchedOnInOnConfiguringDb>(options => options.UseSqlite(connection))));

        Assert.Contains("ClosedTrigger as IAfterSaveTrigger<Note>, OpenTrigger<> as IBeforeSaveTrigger<>.", refused.Message);
        Assert.Contains("Switched on with UseTriggers for: SwitchedOnInOnConfiguringDb.", refused.Message);
    }

    // ── The library switched on for a context ───────────────────────────────────

    [Fact]
    public async Task A_host_whose_context_has_the_library_switched_on_in_OnConfiguring_does_not_start()
    {
        var refused = await DoesNotStartAsync(Host(services => services
            .AddSaveHandler<Converted>()
            .AddDbContext<SwitchedOnInOnConfiguringDb>(options => options.UseSqlite(connection))));

        Assert.Contains("Switched on with UseTriggers for: SwitchedOnInOnConfiguringDb.", refused.Message);
        SaysHowToConvert(refused.Message);
    }

    [Fact]
    public async Task A_host_whose_context_has_the_library_switched_on_where_it_is_registered_does_not_start()
    {
        var refused = await DoesNotStartAsync(new HostBuilder()
            .ConfigureServices(services => services
                .AddSaveHandler<Converted>()
                .AddDbContext<RegisteredDb>(options => options.UseSqlite(connection).UseTriggers()))
            .Build());

        Assert.Contains("Switched on with UseTriggers for: RegisteredDb.", refused.Message);
        SaysHowToConvert(refused.Message);
    }

    [Fact]
    public void A_context_built_without_a_host_refuses_the_library_switched_on_in_its_options_when_it_is_created()
    {
        var options = new DbContextOptionsBuilder<RegisteredDb>().UseSqlite(connection).UseTriggers().Options;

        // EF builds its services for the options a context is constructed with, in the constructor.
        var refused = Assert.ThrowsAny<InvalidOperationException>(() => new RegisteredDb(options));

        Assert.Contains("RegisteredDb has EntityFrameworkCore.Triggered switched on", refused.Message);
        SaysHowToConvert(refused.Message);
    }

    [Fact]
    public async Task A_context_built_without_a_host_refuses_the_library_switched_on_in_OnConfiguring_at_its_first_use()
    {
        var options = new DbContextOptionsBuilder<SwitchedOnInOnConfiguringDb>().UseSqlite(connection).Options;

        // EF calls OnConfiguring at the context's first use, and not in its constructor: a constructor of the app's
        // context has not run yet at that point. So this is where the context can first know.
        await using var db = new SwitchedOnInOnConfiguringDb(options);

        var refused = Assert.ThrowsAny<InvalidOperationException>(() => db.Notes.Add(new Note { Text = "a" }));

        Assert.Contains("SwitchedOnInOnConfiguringDb has EntityFrameworkCore.Triggered switched on", refused.Message);
        SaysHowToConvert(refused.Message);

        // And every time after, also for a second instance: nothing was cached for these options.
        Assert.ThrowsAny<InvalidOperationException>(() => db.Model);

        await using var second = new SwitchedOnInOnConfiguringDb(options);
        Assert.ThrowsAny<InvalidOperationException>(() => second.Model);
    }

    // ── A host with neither ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_host_with_no_trigger_and_no_context_with_the_library_switched_on_starts_and_saves()
    {
        using var host = Host(services => services.AddSaveHandler<Converted>());

        await host.StartAsync(Ct);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RegisteredDb>();
            await db.Database.EnsureCreatedAsync(Ct);

            db.Add(new Note { Text = "a" });
            await db.SaveChangesAsync(Ct);

            Assert.Equal(1, await db.Notes.CountAsync(Ct));
        }

        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task The_check_goes_by_the_assembly_a_type_comes_from_and_not_by_its_name()
    {
        // A class of the app's own that happens to be named like a trigger is not the library's.
        using var host = Host(services => services
            .AddSaveHandler<Converted>()
            .AddScoped<LooksLikeATrigger.IAfterSaveTrigger<Note>, LooksLikeATrigger.Mine>());

        await host.StartAsync(Ct);
        await host.StopAsync(Ct);
    }

    [Theory]
    [InlineData("AddSaveHandler")]
    [InlineData("AddNotifications")]
    [InlineData("AddShiftEntityWeb")]
    [InlineData("AddShiftEntityFunctions")]
    public async Task Each_registration_that_takes_part_in_saves_adds_the_check_once(string registration)
    {
        // The framework's own registration is the one every API and every functions host calls. So a host that has
        // no handler, no notifications and no replication, and still registers a trigger, is stopped too.
        void Register(IServiceCollection services)
        {
            if (registration == "AddSaveHandler")
                services.AddSaveHandler<Converted>().AddSaveHandler<Converted>();
            else if (registration == "AddNotifications")
                services.AddNotifications<RegisteredDb>("Tests", _ => { });
            else if (registration == "AddShiftEntityWeb")
                services.AddControllers().AddShiftEntityWeb().AddShiftEntityWeb();
            else
                services.AddShiftEntityFunctions().AddShiftEntityFunctions();
        }

        using (var clean = Host(Register))
            Assert.Single(clean.Services.GetServices<IHostedService>(), service => service.GetType().Name == "TriggerLibraryCheck");

        var refused = await DoesNotStartAsync(Host(services =>
        {
            Register(services);
            services.AddScoped<IAfterSaveTrigger<Note>, ClosedTrigger>();
        }));

        Assert.Contains("ClosedTrigger as IAfterSaveTrigger<Note>", refused.Message);
    }
}

/// <summary>Types of the test assembly that only look like the library's.</summary>
public static class LooksLikeATrigger
{
    public interface IAfterSaveTrigger<TEntity>;

    public sealed class Mine : IAfterSaveTrigger<TriggerLibraryCheckTests.Note>;
}
