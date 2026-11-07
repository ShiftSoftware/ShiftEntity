using EntityFrameworkCore.Triggered;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Model.Notifications;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.ShiftIdentity.Core;
using ShiftSoftware.ShiftIdentity.Core.DTOs.Company;
using ShiftSoftware.TypeAuth.Core;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;

namespace ShiftSoftware.ShiftEntity.Tests.Notifications;

public class NotificationTestDbContext(DbContextOptions<NotificationTestDbContext> options) : ShiftDbContext(options)
{
    public DbSet<NotifiedRecord> Records => Set<NotifiedRecord>();
    public DbSet<PlainRecord> PlainRecords => Set<PlainRecord>();
}

/// <summary>A record with a company, a branch and watchers.</summary>
public class NotifiedRecord : ShiftEntity<NotifiedRecord>,
    IEntityHasCompany<NotifiedRecord>,
    IEntityHasCompanyBranch<NotifiedRecord>,
    IHasNotificationWatchers
{
    public string? Name { get; set; }
    public string? CustomerEmail { get; set; }
    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }

    /// <summary>What <see cref="GetNotificationWatchers"/> returns. A test sets it before raising.</summary>
    [NotMapped]
    public List<NotificationWatcher> TestWatchers { get; set; } = [];

    [NotMapped]
    public bool WatchersThrow { get; set; }

    public IEnumerable<NotificationWatcher> GetNotificationWatchers()
        => WatchersThrow ? throw new InvalidOperationException("Deliberate watcher failure.") : TestWatchers;
}

/// <summary>The same columns as <see cref="NotifiedRecord"/>, without watchers.</summary>
public class PlainRecord : ShiftEntity<PlainRecord>,
    IEntityHasCompany<PlainRecord>,
    IEntityHasCompanyBranch<PlainRecord>
{
    public string? Name { get; set; }
    public string? CustomerEmail { get; set; }
    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }
}

public class NotifiedRecordDTO : ShiftEntityDTOBase
{
    public override string? ID { get; set; }
}

public sealed class ThrowingNotifiedRecordMapper : IShiftEntityMapper<NotifiedRecord, NotifiedRecordDTO, NotifiedRecordDTO>
{
    public NotifiedRecord MapToEntity(NotifiedRecordDTO dto, NotifiedRecord existing, MappingContext context = default) => throw new NotSupportedException();
    public NotifiedRecordDTO MapToView(NotifiedRecord entity, MappingContext context = default) => throw new NotSupportedException();
    public IQueryable<NotifiedRecordDTO> MapToList(IQueryable<NotifiedRecord> query, MappingContext context = default) => throw new NotSupportedException();
    public void CopyEntity(NotifiedRecord source, NotifiedRecord target, MappingContext context = default) => throw new NotSupportedException();
}

/// <summary>The switches a test flips on its running host.</summary>
public sealed class NotificationTestSettings
{
    /// <summary>Gives the repository an enabled global filter, which refuses CanAccessAs.</summary>
    public bool RepositoryHasGlobalFilter { get; set; }

    /// <summary>The topic the after-save trigger raises, or null for none.</summary>
    public NotificationTopic? RaiseFromTrigger { get; set; }

    public bool ContentReturnsNull { get; set; }
}

public class NotifiedRecordRepository : ShiftRepository<NotificationTestDbContext, NotifiedRecord, NotifiedRecordDTO, NotifiedRecordDTO>
{
    public NotifiedRecordRepository(NotificationTestDbContext db, NotificationTestSettings settings) : base(db, options =>
    {
        options.UseMapper(new ThrowingNotifiedRecordMapper());
        options.DataLevelAccess(access => access.On(ShiftIdentityActions.DataLevelAccess.Companies)
            .Key(record => record.CompanyID).HashId<CompanyDTO>());

        if (settings.RepositoryHasGlobalFilter)
            options.FilterByClaimValues(context => context.ClaimValues!.Contains(context.Entity.Name!));
    })
    {
    }
}

/// <summary>The repository save path with a transaction: having an after-save hook is what selects it.</summary>
public sealed class HookedRecordRepository(NotificationTestDbContext db, NotificationTestSettings settings, Func<NotifiedRecord, ValueTask> afterSave)
    : NotifiedRecordRepository(db, settings), IShiftEntityHasAfterSaveHook<NotifiedRecord>
{
    public ValueTask AfterSaveAsync(NotifiedRecord entity, ActionTypes action) => afterSave(entity);
}

/// <summary>A host's after-save trigger: the <c>if</c> stays in the host, and it raises the topic.</summary>
public sealed class RaisingTrigger(INotificationRaiser raiser, NotificationTestSettings settings) : IAfterSaveTrigger<NotifiedRecord>
{
    public async Task AfterSave(ITriggerContext<NotifiedRecord> context, CancellationToken cancellationToken)
    {
        if (settings.RaiseFromTrigger is { } topic)
            await raiser.RaiseAsync(topic, context.Entity);
    }
}

public sealed class TestEmailContent(NotificationTestSettings settings) : INotificationEmailContent<NotifiedRecord>
{
    public ValueTask<NotificationEmailContent?> BuildAsync(NotificationTopic topic, NotifiedRecord record, CancellationToken cancellationToken = default)
    {
        if (settings.ContentReturnsNull)
            return ValueTask.FromResult<NotificationEmailContent?>(null);

        return ValueTask.FromResult<NotificationEmailContent?>(new NotificationEmailContent
        {
            Subject = topic.ID,
            Headline = $"{record.Name}|{CultureInfo.CurrentUICulture.Name}|{CultureInfo.CurrentCulture.Name}",
            Details = [new("Name", record.Name)],
        });
    }
}

public sealed class FakeEmailSender : INotificationEmailSender
{
    private readonly object gate = new();
    private readonly List<NotificationEmailMessage> sent = [];

    /// <summary>Runs before a message counts as sent. A test uses it to throw or to block.</summary>
    public Func<NotificationEmailMessage, Task>? BeforeSend { get; set; }

    public List<NotificationEmailMessage> Sent
    {
        get
        {
            lock (gate)
                return [.. sent];
        }
    }

    public async Task SendAsync(NotificationEmailMessage message, CancellationToken cancellationToken = default)
    {
        if (BeforeSend is { } before && message.Subject != NotificationTestApp.SentinelTopic.ID)
            await before(message);

        lock (gate)
            sent.Add(message);
    }
}

/// <summary>Stands in for ShiftIdentity's documents. Grants are real TypeAuth contexts.</summary>
public sealed class FakeIdentityDirectory : INotificationIdentityDirectory
{
    public Dictionary<long, NotificationUser> Users { get; } = [];
    public Dictionary<long, string> NotEligible { get; } = [];
    public Dictionary<long, string> Unresolved { get; } = [];
    public Dictionary<long, IReadOnlyList<long>?> Teams { get; } = [];
    public Dictionary<long, IReadOnlyList<TaggedTextDTO>> Branches { get; } = [];
    public List<long> BranchesRead { get; } = [];

    public Task<NotificationUserLookup> LookUpUsersAsync(IReadOnlyCollection<long> userIDs, CancellationToken cancellationToken = default)
        => Task.FromResult(new NotificationUserLookup(
            userIDs.Where(Users.ContainsKey).Select(id => Users[id]).ToList(),
            userIDs.Where(NotEligible.ContainsKey).ToDictionary(id => id, id => NotEligible[id]),
            userIDs.Where(Unresolved.ContainsKey).ToDictionary(id => id, id => Unresolved[id])));

    public Task<IReadOnlyList<long>?> GetTeamMembersAsync(long teamID, CancellationToken cancellationToken = default)
        => Task.FromResult(Teams.GetValueOrDefault(teamID));

    public Task<IReadOnlyList<TaggedTextDTO>?> GetBranchEmailsAsync(long branchID, CancellationToken cancellationToken = default)
    {
        BranchesRead.Add(branchID);
        return Task.FromResult(Branches.GetValueOrDefault(branchID));
    }
}

public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly object gate = new();
    private readonly List<(LogLevel Level, string Message)> lines = [];

    public List<(LogLevel Level, string Message)> Lines
    {
        get
        {
            lock (gate)
                return [.. lines];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }

    private sealed class Logger(RecordingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.gate)
                owner.lines.Add((logLevel, formatter(state, exception)));
        }
    }
}

public class NotificationTestTopics
{
    public static readonly NotificationTopic Staff = new()
    {
        ID = "Sales.Invoice.Created",
        DisplayName = "Invoice created",
        Description = "A new invoice was saved.",
        ReadPermission = NotificationTestActions.Invoices,
        AllowEmails = true,
    };

    public static readonly NotificationTopic StaffUsersOnly = new()
    {
        ID = "Sales.Invoice.Approved",
        DisplayName = "Invoice approved",
        Description = "An invoice was approved.",
        ReadPermission = NotificationTestActions.Invoices,
    };

    public static readonly NotificationTopic Customers = new()
    {
        ID = "Sales.Invoice.Paid",
        DisplayName = "Invoice paid",
        Description = "Tells the customer that the invoice was paid.",
        ForCustomers = true,
    };

    public static readonly NotificationTopic Sentinel = new()
    {
        ID = "Sales.Test.Sentinel",
        DisplayName = "Sentinel",
        Description = "Proves that everything raised before it was handled.",
        ReadPermission = NotificationTestActions.Invoices,
        AllowEmails = true,
    };
}

/// <summary>
/// A running host: a real context on a temporary SQLite file, a real repository, the real queue and pipeline, and fakes
/// for the sender and Identity. A test that expects nothing to be sent calls <see cref="DrainAsync"/> first. It raises a
/// sentinel event and waits for it, and the queue is first in, first out, so anything raised earlier was handled by then.
/// </summary>
public sealed class NotificationTestApp : IAsyncDisposable
{
    public static readonly NotificationTopic SentinelTopic = NotificationTestTopics.Sentinel;

    private const string SentinelAddress = "sentinel@example.com";
    private static readonly NotificationSubscribersEntryDTO SentinelEntry = new()
    {
        Topic = SentinelTopic.ID,
        Where = NotificationWhere.All(),
        Emails = new() { To = [SentinelAddress] },
    };

    private readonly string file;
    private readonly List<IHostedService> started;
    private int sentinels;

    private NotificationTestApp(ServiceProvider provider, string file, List<IHostedService> started)
    {
        Provider = provider;
        this.file = file;
        this.started = started;
        SetEntries();
    }

    public ServiceProvider Provider { get; }
    public FakeEmailSender Sender => Provider.GetRequiredService<FakeEmailSender>();
    public FakeIdentityDirectory Directory => Provider.GetRequiredService<FakeIdentityDirectory>();
    public NotificationTestSettings Settings => Provider.GetRequiredService<NotificationTestSettings>();
    public RecordingLoggerProvider Log => Provider.GetRequiredService<RecordingLoggerProvider>();
    public static readonly IHashIdService HashIds = BuildHashIds();

    private static IHashIdService BuildHashIds()
    {
        var options = new ShiftEntityOptions();
        options.HashId.RegisterIdentityHashId("a test salt", minHashLength: 5);
        return new HashIdService(Options.Create(options));
    }

    public static string Hash(JsonHashIdConverterAttribute kind, long id) => HashIds.Encode(id, kind);

    /// <summary>The emails a test caused, without the sentinels.</summary>
    public List<NotificationEmailMessage> Sent => Sender.Sent.Where(message => message.Subject != SentinelTopic.ID).ToList();

    public static async Task<NotificationTestApp> StartAsync(
        Action<NotificationOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        bool withDirectory = true,
        bool withSender = true,
        bool withContent = true)
    {
        var file = Path.Combine(Path.GetTempPath(), $"notification-tests-{Guid.NewGuid():N}.db");
        var collection = new ServiceCollection();

        collection.AddSingleton<RecordingLoggerProvider>();
        collection.AddLogging(logging => logging.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<RecordingLoggerProvider>()));

        collection.AddDbContext<NotificationTestDbContext>(options => options
            .UseSqlite($"Data Source={file};Pooling=False")
            .UseTriggers());

        // What the framework's registrations contribute, minimally.
        collection.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.Anonymous());
        collection.AddScoped<IdentityClaimProvider>();
        collection.AddSingleton(HashIds);
        collection.AddSingleton<IDefaultDataLevelAccess>(new RecordingDefaultDataLevelAccess());

        collection.AddSingleton<NotificationTestSettings>();
        collection.AddSingleton<FakeEmailSender>();
        collection.AddSingleton<FakeIdentityDirectory>();
        collection.AddSingleton<InMemoryNotificationSubscribersList>();
        collection.AddSingleton<INotificationSubscribersList>(sp => sp.GetRequiredService<InMemoryNotificationSubscribersList>());

        collection.AddScoped<IShiftEntityFind<NotifiedRecord>, NotifiedRecordRepository>();
        collection.AddTransient<IAfterSaveTrigger<NotifiedRecord>, RaisingTrigger>();

        collection.AddNotifications<NotificationTestDbContext>("Sales", options =>
        {
            options.AddTopics<NotificationTestTopics>();
            configure?.Invoke(options);
        });

        if (withSender)
            collection.AddSingleton<INotificationEmailSender>(sp => sp.GetRequiredService<FakeEmailSender>());

        // The sentinel always needs content, so a test without content removes it for the record it raises on.
        if (withContent)
            collection.AddScoped<INotificationEmailContent<NotifiedRecord>, TestEmailContent>();

        if (withDirectory)
            collection.AddScoped<INotificationIdentityDirectory>(sp => sp.GetRequiredService<FakeIdentityDirectory>());

        services?.Invoke(collection);

        var provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>().Database.EnsureCreatedAsync();

        var hosted = provider.GetServices<IHostedService>().ToList();

        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);

        return new(provider, file, hosted);
    }

    public void SetEntries(params NotificationSubscribersEntryDTO[] entries)
        => Provider.GetRequiredService<InMemoryNotificationSubscribersList>().SetEntries([SentinelEntry, .. entries]);

    /// <summary>A user with this read permission and data-level grants, and a verified email.</summary>
    public static NotificationUser User(long id, bool canRead = true, bool canOpenRecord = true, string? email = null, bool emailVerified = true)
    {
        var dataLevel = canOpenRecord ? """{"Companies":["Read"]}""" : """{}""";
        var screen = canRead ? """{"Invoices":["Read","Write","Delete"]}""" : """{}""";
        var tree = "{\"NotificationTestActions\":" + screen + ",\"ShiftIdentityActions\":{\"DataLevelAccess\":" + dataLevel + "}}";

        var typeAuth = new TypeAuthContextBuilder()
            .AddActionTree<NotificationTestActions>()
            .AddActionTree<ShiftIdentityActions>()
            .AddAccessTree(tree)
            .Build();

        return new(id, typeAuth, FakeCurrentUserProvider.WithClaims(), email ?? $"user{id}@example.com", emailVerified);
    }

    /// <summary>Saves a record, and returns it as the host's trigger or hook would have it.</summary>
    public async Task<NotifiedRecord> SaveAsync(NotifiedRecord? record = null)
    {
        record ??= new NotifiedRecord { Name = "Invoice", CompanyID = 4, CompanyBranchID = 8 };

        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();

        db.Records.Add(record);
        await db.SaveChangesAsync();

        return record;
    }

    public async Task RaiseAsync(NotificationTopic topic, NotifiedRecord record, IEnumerable<long>? except = null)
    {
        await using var scope = Provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<INotificationRaiser>().RaiseAsync(topic, record, except);
    }

    /// <summary>Saves a record, raises the topic on it, and waits for it to be handled.</summary>
    public async Task<List<NotificationEmailMessage>> SaveRaiseAndDrainAsync(NotificationTopic topic, NotifiedRecord? record = null, IEnumerable<long>? except = null)
    {
        var saved = await SaveAsync(record);
        await RaiseAsync(topic, saved, except);
        await DrainAsync();

        return Sent;
    }

    /// <param name="savedRecord">A record to raise the sentinel on, for when a test's open transaction blocks a new save.</param>
    public async Task DrainAsync(NotifiedRecord? savedRecord = null)
    {
        var sentinel = savedRecord ?? new NotifiedRecord { Name = "Sentinel" };
        var expected = Interlocked.Increment(ref sentinels);

        if (savedRecord is null)
            await SaveWithoutTriggerAsync(sentinel);

        await RaiseAsync(SentinelTopic, sentinel);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (Sender.Sent.Count(message => message.Subject == SentinelTopic.ID) < expected)
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("The sentinel event was not handled in time. Log: " + string.Join(" | ", Log.Lines.Select(line => line.Message)));

            await Task.Delay(10);
        }
    }

    private async Task SaveWithoutTriggerAsync(NotifiedRecord record)
    {
        var raise = Settings.RaiseFromTrigger;
        Settings.RaiseFromTrigger = null;

        try
        {
            await SaveAsync(record);
        }
        finally
        {
            Settings.RaiseFromTrigger = raise;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        foreach (var service in started)
            await service.StopAsync(stopTimeout.Token);

        await Provider.DisposeAsync();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // A leftover file in the temp folder harms nothing.
        }
    }
}
