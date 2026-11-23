using EntityFrameworkCore.Triggered;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model.Notifications;
using Xunit;
using Topics = ShiftSoftware.ShiftEntity.Tests.Notifications.NotificationTestTopics;

namespace ShiftSoftware.ShiftEntity.Tests.Notifications;

/// <summary>
/// Raise rules in place of triggers: RaiseWhenCreated, an entity's own rules and rule classes in DI, run by the save
/// observer after each save of the host's context, once the save has written its rows. Also the team watcher and the
/// warning when nobody passes.
/// </summary>
public class NotificationRuleTests
{
    private const string ListAddress = "list@example.com";

    private static NotificationSubscribersEntryDTO StaffEmails() => new()
    {
        Topic = Topics.Staff.ID,
        Where = NotificationWhere.All(),
        Emails = new() { To = [ListAddress] },
    };

    private static void PlainRecordContent(IServiceCollection services)
        => services.AddScoped<INotificationEmailContent<PlainRecord>, PlainRecordEmailContent>();

    private static async Task<long> CreateAsync<TEntity>(NotificationTestApp app, TEntity record)
        where TEntity : ShiftEntity<TEntity>
    {
        await using var scope = app.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
        db.Add(record);
        await db.SaveChangesAsync();
        return record.ID;
    }

    private static async Task UpdateAsync<TEntity>(NotificationTestApp app, long id, Action<TEntity> change)
        where TEntity : ShiftEntity<TEntity>
    {
        await using var scope = app.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
        var record = await db.Set<TEntity>().SingleAsync(x => x.ID == id);
        change(record);
        await db.SaveChangesAsync();
    }

    // ── RaiseWhenCreated ────────────────────────────────────────────────────────

    [Fact]
    public async Task RaiseWhenCreated_RaisesOnTheSaveThatCreatesTheRecord_AndNotOnLaterSaves()
    {
        await using var app = await NotificationTestApp.StartAsync(
            options => options.RaiseWhenCreated<PlainRecord>(Topics.Staff), PlainRecordContent);
        app.SetEntries(StaffEmails());

        var id = await CreateAsync(app, new PlainRecord { Name = "New" });
        await app.DrainAsync();
        Assert.Single(app.Sent);

        await UpdateAsync<PlainRecord>(app, id, record => record.Name = "Renamed");
        await app.DrainAsync();
        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task RaiseWhenCreated_ForABaseClass_AppliesToTheRecordsThatHaveIt()
    {
        await using var app = await NotificationTestApp.StartAsync(
            options => options.RaiseWhenCreated<ShiftEntity<PlainRecord>>(Topics.Staff), PlainRecordContent);
        app.SetEntries(StaffEmails());

        await CreateAsync(app, new PlainRecord { Name = "New" });
        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public void RaiseWhenCreated_WithATopicThatIsNotRegistered_FailsAtStartup()
    {
        var unknown = new NotificationTopic
        {
            ID = "Sales.Invoice.Unknown",
            DisplayName = "Unknown",
            Description = "Never registered.",
            ReadPermission = NotificationTestActions.Invoices,
        };

        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddNotifications<NotificationTestDbContext>("Sales", options => options
                .AddTopics<Topics>()
                .RaiseWhenCreated<PlainRecord>(unknown)));

        Assert.Contains("Sales.Invoice.Unknown", failure.Message);
    }

    // ── The entity's own rules ──────────────────────────────────────────────────

    [Fact]
    public async Task EntityRule_Became_RaisesOnlyOnTheSaveThatMakesItTrue()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(StaffEmails());

        var id = await CreateAsync(app, new NotifiedRecord { Name = "Draft" });
        await app.DrainAsync();
        Assert.Empty(app.Sent);

        await UpdateAsync<NotifiedRecord>(app, id, record => record.Name = NotifiedRecord.ApprovedName);
        await app.DrainAsync();
        Assert.Single(app.Sent);

        await UpdateAsync<NotifiedRecord>(app, id, record => record.CustomerEmail = "someone@example.com");
        await app.DrainAsync();
        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task EntityRule_Became_OnARecordCreatedInThatState_Raises()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(StaffEmails());

        await CreateAsync(app, new NotifiedRecord { Name = NotifiedRecord.ApprovedName });
        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task EntityRule_OnTheRepositoryPathWithATransaction_RaisesAfterTheCommit()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(StaffEmails());
        var id = await CreateAsync(app, new NotifiedRecord { Name = "Draft" });

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var repository = new HookedRecordRepository(db, app.Settings, _ => ValueTask.CompletedTask);

            var record = await db.Records.SingleAsync(x => x.ID == id);
            record.Name = NotifiedRecord.ApprovedName;
            await repository.SaveChangesAsync();
        }

        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task EntityRule_InARolledBackTransaction_RaisesNothing()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(StaffEmails());
        var id = await CreateAsync(app, new NotifiedRecord { Name = "Draft" });

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();

            var record = await db.Records.SingleAsync(x => x.ID == id);
            record.Name = NotifiedRecord.ApprovedName;
            await db.SaveChangesAsync();

            await transaction.RollbackAsync();
        }

        await app.DrainAsync();

        Assert.Empty(app.Sent);
    }

    [Fact]
    public async Task EntityRule_UnderSuppressNotifications_RaisesNothing_UntilTheLastScopeEnds()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(StaffEmails());

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();

            using (db.SuppressNotifications())
            {
                using (db.SuppressNotifications())
                {
                    db.Records.Add(new NotifiedRecord { Name = NotifiedRecord.ApprovedName });
                    await db.SaveChangesAsync();
                }

                // The inner scope has ended, and the outer one still holds.
                db.Records.Add(new NotifiedRecord { Name = NotifiedRecord.ApprovedName });
                await db.SaveChangesAsync();
            }

            db.Records.Add(new NotifiedRecord { Name = NotifiedRecord.ApprovedName });
            await db.SaveChangesAsync();
        }

        await app.DrainAsync();

        // Only the save after both scopes raised.
        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task EntityRule_UnderSuppressReplication_StillRaises()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(StaffEmails());

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            using (db.SuppressReplication())
            {
                db.Records.Add(new NotifiedRecord { Name = NotifiedRecord.ApprovedName });
                await db.SaveChangesAsync();
            }
        }

        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task EntityRule_ThatThrows_IsLogged_AndTheSaveSucceeds()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(StaffEmails());

        var id = await CreateAsync(app, new NotifiedRecord { Name = NotifiedRecord.RuleThrowsName });
        await app.DrainAsync();

        Assert.True(id > 0);
        Assert.Empty(app.Sent);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("failed on NotifiedRecord"));
    }

    [Fact]
    public async Task EntityRule_WhenAnAfterSaveStepSavesTheRecordAgain_RaisesOnceForTheSaveThatMadeItTrue()
    {
        // A host trigger that changes the record and saves it again from its after-save step, as a push that stores its
        // outcome does. The outer save is asked about what it wrote, not about the record the inner save changed.
        await using var app = await NotificationTestApp.StartAsync(
            services: services => services.AddTransient<IAfterSaveTrigger<NotifiedRecord>, ApprovingTrigger>());
        app.SetEntries(StaffEmails());

        var id = await CreateAsync(app, new NotifiedRecord { Name = ApprovingTrigger.ApproveMe });
        await app.DrainAsync();

        Assert.Single(app.Sent);

        await using var scope = app.Provider.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>().Records.SingleAsync(x => x.ID == id);
        Assert.Equal(NotifiedRecord.ApprovedName, saved.Name);
    }

    // ── Rule classes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RuleClass_UsesItsServices()
    {
        await using var app = await NotificationTestApp.StartAsync(
            options => options.AddRule<PlainRecord, PlainRecordRule>(),
            services =>
            {
                PlainRecordContent(services);
                services.AddSingleton(new PlainRecordRuleSettings { Name = "Ready" });
            });
        app.SetEntries(StaffEmails());

        await CreateAsync(app, new PlainRecord { Name = "Not ready" });
        await CreateAsync(app, new PlainRecord { Name = "Ready" });
        await app.DrainAsync();

        var sent = Assert.Single(app.Sent);
        Assert.Contains("Ready", sent.HtmlBody);
    }

    [Fact]
    public async Task RuleClassAndRaiseWhenCreated_ReturningTheSameTopic_RaiseItOnce()
    {
        await using var app = await NotificationTestApp.StartAsync(
            options => options
                .RaiseWhenCreated<PlainRecord>(Topics.Staff)
                .AddRule<PlainRecord, PlainRecordRule>(),
            services =>
            {
                PlainRecordContent(services);
                services.AddSingleton(new PlainRecordRuleSettings { Name = "Ready" });
            });
        app.SetEntries(StaffEmails());

        await CreateAsync(app, new PlainRecord { Name = "Ready" });
        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public void AddRule_RegistersTheRuleClassInDI()
    {
        var services = new ServiceCollection().AddNotifications<NotificationTestDbContext>("Sales", options => options
            .AddTopics<Topics>()
            .AddRule<PlainRecord, PlainRecordRule>());

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(PlainRecordRule) && descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    // ── Values from before the save ─────────────────────────────────────────────

    [Fact]
    public async Task EntityRule_WhenABeforeSaveStepMakesTheChange_StillRaises()
    {
        // The record is tracked and unchanged when the save starts. A before-save trigger of another row sets the name
        // the rule waits for, so the record only becomes modified inside the save.
        await using var app = await NotificationTestApp.StartAsync(
            services: services => services.AddTransient<IBeforeSaveTrigger<PlainRecord>, ApprovingBeforeSaveTrigger>());
        app.SetEntries(StaffEmails());
        var id = await CreateAsync(app, new NotifiedRecord { Name = "Draft" });

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            await db.Records.SingleAsync(x => x.ID == id);
            db.Add(new PlainRecord { Name = "Its before-save step approves the record" });
            await db.SaveChangesAsync();
        }

        await app.DrainAsync();

        Assert.Single(app.Sent);
        Assert.DoesNotContain(app.Log.Lines, line => line.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task EntityRule_WithChangeDetectionSwitchedOff_AndATriggerLibraryThatDetects_Raises()
    {
        // The trigger library detects changes itself, so the change is written although the caller switched detection
        // off. What is written is what the rules are asked about.
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(StaffEmails());
        var id = await CreateAsync(app, new NotifiedRecord { Name = "Draft" });

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var record = await db.Records.SingleAsync(x => x.ID == id);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            record.Name = NotifiedRecord.ApprovedName;
            await db.SaveChangesAsync();
        }

        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task EntityRule_OnARecordAttachedAsModified_HasNothingToCompare_AndRaisesNothing()
    {
        // Update() attaches the record as modified without loading it, so the context's picture of the row before the
        // save is what the caller passed in. Nothing "became" anything. Documented on NotificationChange.Original.
        await using var app = await NotificationTestApp.StartAsync(withTriggers: false);
        app.SetEntries(StaffEmails());
        var id = await CreateAsync(app, new NotifiedRecord { Name = "Draft" });

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            db.Update(new NotifiedRecord { ID = id, Name = NotifiedRecord.ApprovedName });
            await db.SaveChangesAsync();
        }

        await app.DrainAsync();

        Assert.Empty(app.Sent);
    }

    [Fact]
    public async Task ASaveThatDoesNotAcceptItsChanges_RaisesNothing()
    {
        // EF's pattern for a transaction under a retrying execution strategy. The save is never reported, so no rule is
        // asked. Documented on IHasNotificationRules.
        await using var app = await NotificationTestApp.StartAsync(withTriggers: false);
        app.SetEntries(StaffEmails());

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Add(new NotifiedRecord { Name = NotifiedRecord.ApprovedName });
            await db.SaveChangesAsync(acceptAllChangesOnSuccess: false);
            await transaction.CommitAsync();
            db.ChangeTracker.AcceptAllChanges();
        }

        await app.DrainAsync();

        Assert.Empty(app.Sent);
    }

    // ── Where the rules run ─────────────────────────────────────────────────────

    [Fact]
    public async Task RuleClass_ThatSavesThroughTheSavingContext_WritesOnlyItsOwnRow()
    {
        // The rules run once EF has accepted every row of the save. While they ran as EF accepted each row, a rule that
        // saved wrote the rows EF hadn't accepted yet a second time: three new records became six.
        await using var app = await NotificationTestApp.StartAsync(
            options => options.AddRule<PlainRecord, NoteWritingRule>(), withTriggers: false);

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            db.AddRange(new PlainRecord { Name = "A" }, new PlainRecord { Name = "B" }, new PlainRecord { Name = "C" });
            await db.SaveChangesAsync();
        }

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            Assert.Equal(["A", "B", "C"], await db.PlainRecords.OrderBy(x => x.ID).Select(x => x.Name).ToListAsync());
            Assert.Equal(3, await db.Records.CountAsync(x => x.Name!.StartsWith("note about")));
        }
    }

    [Fact]
    public async Task EntityRule_WhenASaveHandlerSavesTheRecordAgain_RaisesOnceForTheSaveThatMadeItTrue()
    {
        // A host's save handler that changes the record and saves it again, as a push that stores its outcome does. The
        // rules of the first save run before the handler, on what that save wrote. The handler's save is a save of its
        // own, asked about on its own.
        await using var app = await NotificationTestApp.StartAsync(
            services: services => services.AddSaveHandler<NotifiedRecord, ApprovingSaveHandler>(), withTriggers: false);
        app.SetEntries(StaffEmails());

        var id = await CreateAsync(app, new NotifiedRecord { Name = ApprovingSaveHandler.ApproveMe });
        await app.DrainAsync();

        Assert.Single(app.Sent);

        await using var scope = app.Provider.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>().Records.SingleAsync(x => x.ID == id);
        Assert.Equal(NotifiedRecord.ApprovedName, saved.Name);
    }

    // ── Classes derived from an entity ──────────────────────────────────────────

    [Fact]
    public async Task ARecordOfADerivedClass_IsRaisedAsTheEntityItDerivesFrom()
    {
        // The rules and the email content are ParentRecord's. A ChildRecord is a row of its table, so it is read back,
        // checked and written about as a ParentRecord.
        await using var app = await NotificationTestApp.StartAsync(
            options => options.RaiseWhenCreated<ParentRecord>(Topics.Staff),
            services => services.AddScoped<INotificationEmailContent<ParentRecord>, ParentRecordEmailContent>());
        app.SetEntries(StaffEmails());

        var id = await CreateAsync<ParentRecord>(app, new ChildRecord { Name = "Draft", Extra = "of its own" });
        await app.DrainAsync();

        // Created: the one-line rule on the base class.
        Assert.Contains("ChildRecord Draft", Assert.Single(app.Sent).HtmlBody);

        // Became approved: the rule the base class has.
        await UpdateAsync<ParentRecord>(app, id, record => record.Name = ParentRecord.ApprovedName);
        await app.DrainAsync();

        Assert.Equal(2, app.Sent.Count);
        Assert.DoesNotContain(app.Log.Lines, line => line.Message.Contains("nothing is raised on it"));
    }

    [Fact]
    public async Task ASave_LeavesChangeDetectionAsTheCallerSetIt()
    {
        await using var app = await NotificationTestApp.StartAsync();
        var id = await CreateAsync(app, new NotifiedRecord { Name = "Draft" });

        await using var scope = app.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
        (await db.Records.SingleAsync(x => x.ID == id)).Name = "Renamed";
        await db.SaveChangesAsync();

        Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
    }

    [Fact]
    public async Task WithChangeDetectionSwitchedOff_TheSaveLooksForNoChangeItWasNotAskedToWrite()
    {
        // The caller switched detection off and didn't detect, so EF writes nothing. Looking for old values must not change
        // that by detecting the change itself. The trigger library detects changes on its own, so this host runs without it.
        await using var app = await NotificationTestApp.StartAsync(withTriggers: false);
        app.SetEntries(StaffEmails());
        var id = await CreateAsync(app, new NotifiedRecord { Name = "Draft" });

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var record = await db.Records.SingleAsync(x => x.ID == id);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            record.Name = NotifiedRecord.ApprovedName;
            await db.SaveChangesAsync();

            Assert.False(db.ChangeTracker.AutoDetectChangesEnabled);
        }

        await app.DrainAsync();
        Assert.Empty(app.Sent);

        await using (var scope = app.Provider.CreateAsyncScope())
            Assert.Equal("Draft", (await scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>().Records.SingleAsync(x => x.ID == id)).Name);
    }

    // ── Team watchers ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TeamWatcher_EachMemberIsChecked_LikeAnyUser()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Teams[7] = [1, 2];
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.Directory.Users[2] = NotificationTestApp.User(2, canOpenRecord: false);

        var record = new NotifiedRecord { Name = "Invoice", CompanyID = 4, TestWatchers = [NotificationWatcher.Team(7, Topics.StaffUsersOnly)] };
        var sent = await app.SaveRaiseAndDrainAsync(Topics.StaffUsersOnly, record);

        var message = Assert.Single(sent);
        Assert.Equal(new[] { "user1@example.com" }, message.To);
    }

    [Fact]
    public async Task TeamWatcher_WithMembersNobodyKnows_IsLogged_AndTheOtherWatchersStillHear()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Teams[8] = null;
        app.Directory.Users[1] = NotificationTestApp.User(1);

        var record = new NotifiedRecord
        {
            Name = "Invoice",
            CompanyID = 4,
            TestWatchers = [NotificationWatcher.Team(8, Topics.StaffUsersOnly), NotificationWatcher.User(1, Topics.StaffUsersOnly)],
        };
        var sent = await app.SaveRaiseAndDrainAsync(Topics.StaffUsersOnly, record);

        Assert.Equal(new[] { "user1@example.com" }, Assert.Single(sent).To);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("members of team 8 aren't known"));
    }

    [Fact]
    public async Task TeamWatcher_WithNoTeam_HearsNothing()
    {
        await using var app = await NotificationTestApp.StartAsync();

        var record = new NotifiedRecord { Name = "Invoice", CompanyID = 4, TestWatchers = [NotificationWatcher.Team(null, Topics.StaffUsersOnly)] };
        var sent = await app.SaveRaiseAndDrainAsync(Topics.StaffUsersOnly, record);

        Assert.Empty(sent);
    }

    [Fact]
    public async Task TeamWatcher_OnACustomerTopic_IsIgnored_AndLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Teams[7] = [1];
        app.Directory.Users[1] = NotificationTestApp.User(1);

        var record = new NotifiedRecord
        {
            Name = "Invoice",
            TestWatchers = [NotificationWatcher.Team(7, Topics.Customers), NotificationWatcher.Customer("customer@example.com", Topics.Customers)],
        };
        var sent = await app.SaveRaiseAndDrainAsync(Topics.Customers, record);

        Assert.Equal(new[] { "customer@example.com" }, Assert.Single(sent).To);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("team watcher 7"));
    }

    // ── Nobody passes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task NobodyPasses_LogsOneWarningForTheEvent()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[2] = NotificationTestApp.User(2, canOpenRecord: false);

        var record = new NotifiedRecord { Name = "Invoice", CompanyID = 4, TestWatchers = [NotificationWatcher.User(2, Topics.StaffUsersOnly)] };
        var sent = await app.SaveRaiseAndDrainAsync(Topics.StaffUsersOnly, record);

        Assert.Empty(sent);
        Assert.Single(app.Log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("none of the 1 user(s)"));
    }

    [Fact]
    public async Task NobodyCanSignIn_LogsOneWarningForTheEvent()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.NotEligible[3] = "inactive";

        var record = new NotifiedRecord { Name = "Invoice", CompanyID = 4, TestWatchers = [NotificationWatcher.User(3, Topics.StaffUsersOnly)] };
        var sent = await app.SaveRaiseAndDrainAsync(Topics.StaffUsersOnly, record);

        Assert.Empty(sent);
        Assert.Single(app.Log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("none of the 1 user(s)"));
    }

    [Fact]
    public async Task SomeonePasses_LogsNoSuchWarning()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.Directory.Users[2] = NotificationTestApp.User(2, canOpenRecord: false);

        var record = new NotifiedRecord
        {
            Name = "Invoice",
            CompanyID = 4,
            TestWatchers = [NotificationWatcher.User(1, Topics.StaffUsersOnly), NotificationWatcher.User(2, Topics.StaffUsersOnly)],
        };
        await app.SaveRaiseAndDrainAsync(Topics.StaffUsersOnly, record);

        Assert.DoesNotContain(app.Log.Lines, line => line.Message.Contains("none of the"));
    }
}

public sealed class PlainRecordEmailContent : INotificationEmailContent<PlainRecord>
{
    public ValueTask<NotificationEmailContent?> BuildAsync(NotificationTopic topic, PlainRecord record, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<NotificationEmailContent?>(new NotificationEmailContent
        {
            Subject = topic.ID,
            Headline = record.Name ?? "",
            Details = [new("Name", record.Name)],
        });
}

public sealed class PlainRecordRuleSettings
{
    public string? Name { get; set; }
}

/// <summary>A rule in DI: raises the staff topic when a record becomes the name its settings hold.</summary>
public sealed class PlainRecordRule(PlainRecordRuleSettings settings) : INotificationRule<PlainRecord>
{
    public IEnumerable<NotificationTopic> GetTopicsToRaise(NotificationChange<PlainRecord> change)
    {
        if (change.Became(record => record.Name == settings.Name))
            yield return Topics.Staff;
    }
}

/// <summary>A host's after-save trigger that approves a record and saves it again, from inside the first save.</summary>
public sealed class ApprovingTrigger(NotificationTestDbContext db) : IAfterSaveTrigger<NotifiedRecord>
{
    public const string ApproveMe = "Approve me after the save";

    public async Task AfterSave(ITriggerContext<NotifiedRecord> context, CancellationToken cancellationToken)
    {
        if (context.Entity.Name != ApproveMe)
            return;

        context.Entity.Name = NotifiedRecord.ApprovedName;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>A host's before-save trigger that approves every record the context tracks.</summary>
public sealed class ApprovingBeforeSaveTrigger(NotificationTestDbContext db) : IBeforeSaveTrigger<PlainRecord>
{
    public Task BeforeSave(ITriggerContext<PlainRecord> context, CancellationToken cancellationToken)
    {
        foreach (var record in db.Records.Local.ToList())
            record.Name = NotifiedRecord.ApprovedName;

        return Task.CompletedTask;
    }
}

/// <summary>A host's save handler that approves a record and saves it again, after the first save's rules have run.</summary>
public sealed class ApprovingSaveHandler(NotificationTestDbContext db) : ISaveHandler<NotifiedRecord>
{
    public const string ApproveMe = "Approve me after the save";

    public async ValueTask HandleAsync(SavedRow<NotifiedRecord> row, CancellationToken cancellationToken)
    {
        if (row.Entity.Name != ApproveMe)
            return;

        row.Entity.Name = NotifiedRecord.ApprovedName;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>A rule that writes a note through the scope's context, which is the saving one, and saves it.</summary>
public sealed class NoteWritingRule(NotificationTestDbContext db) : INotificationRule<PlainRecord>
{
    public IEnumerable<NotificationTopic> GetTopicsToRaise(NotificationChange<PlainRecord> change)
    {
        db.Add(new NotifiedRecord { Name = "note about " + change.Entity.Name });
        db.SaveChanges();
        return [];
    }
}
