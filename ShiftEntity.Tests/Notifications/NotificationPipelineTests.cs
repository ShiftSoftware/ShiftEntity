using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Notifications;
using System.Globalization;
using Xunit;
using Topics = ShiftSoftware.ShiftEntity.Tests.Notifications.NotificationTestTopics;

namespace ShiftSoftware.ShiftEntity.Tests.Notifications;

/// <summary>The whole send path, through the real queue and pipeline, with a fake sender and a fake Identity.</summary>
public class NotificationPipelineTests
{
    private static string UserHash(long id) => NotificationTestApp.Hash(NotificationIdentityHashIds.User, id);
    private static string TeamHash(long id) => NotificationTestApp.Hash(NotificationIdentityHashIds.Team, id);
    private static string CompanyHash(long id) => NotificationTestApp.Hash(NotificationIdentityHashIds.Company, id);

    private static NotificationSubscribersEntryDTO UsersEntry(params long[] users) => new()
    {
        Topic = Topics.Staff.ID,
        Users = users.Select(UserHash).ToList(),
    };

    private static NotificationSubscribersEntryDTO EmailsEntry(string? to = null, string? cc = null, string? bcc = null, NotificationWhere? where = null) => new()
    {
        Topic = Topics.Staff.ID,
        Where = where ?? NotificationWhere.All(),
        Emails = new()
        {
            To = to is null ? [] : [to],
            Cc = cc is null ? [] : [cc],
            Bcc = bcc is null ? [] : [bcc],
        },
    };

    // ── Commit ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RepositoryPathWithTransaction_SendsAfterTheCommit()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var raiser = scope.ServiceProvider.GetRequiredService<INotificationRaiser>();
            var repository = new HookedRecordRepository(db, app.Settings, record => new(raiser.RaiseAsync(Topics.Staff, record)));

            db.Records.Add(new NotifiedRecord { Name = "Invoice" });
            await repository.SaveChangesAsync();
        }

        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task RepositoryPathWithTransaction_RolledBackSave_SendsNothing()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var raiser = scope.ServiceProvider.GetRequiredService<INotificationRaiser>();

            // The hook raises, then fails, so the repository rolls its transaction back.
            var repository = new HookedRecordRepository(db, app.Settings, async record =>
            {
                await raiser.RaiseAsync(Topics.Staff, record);
                throw new InvalidOperationException("Deliberate hook failure.");
            });

            db.Records.Add(new NotifiedRecord { Name = "Invoice" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveChangesAsync());
        }

        await app.DrainAsync();

        Assert.Empty(app.Sent);
    }

    [Fact]
    public async Task RepositoryPathWithoutTransaction_RaisedFromATrigger_Sends()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));
        app.Settings.RaiseFromTrigger = Topics.Staff;

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();

            db.Records.Add(new NotifiedRecord { Name = "Invoice" });
            await new NotifiedRecordRepository(db, app.Settings).SaveChangesAsync();
        }

        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task RepositoryPathWithoutTransaction_FailedSave_SendsNothing()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));
        var existing = await app.SaveAsync();
        app.Settings.RaiseFromTrigger = Topics.Staff;

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();

            // The same primary key again: the database refuses the insert.
            db.Records.Add(new NotifiedRecord { ID = existing.ID, Name = "Duplicate" });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => new NotifiedRecordRepository(db, app.Settings).SaveChangesAsync());
        }

        await app.DrainAsync();

        Assert.Empty(app.Sent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepositoryPathInsideACallersTransaction_FollowsThatTransaction(bool commit)
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));
        app.Settings.RaiseFromTrigger = Topics.Staff;

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync();

            db.Records.Add(new NotifiedRecord { Name = "Invoice" });
            await new NotifiedRecordRepository(db, app.Settings).SaveChangesAsync();

            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }

        app.Settings.RaiseFromTrigger = null;
        await app.DrainAsync();

        Assert.Equal(commit ? 1 : 0, app.Sent.Count);
    }

    [Fact]
    public async Task PlainSaveChanges_InAnOpenTransaction_WaitsForTheCommit()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        // Saved first, because SQLite allows one writer and the transaction below holds it.
        var sentinelRecord = await app.SaveAsync(new NotifiedRecord { Name = "Sentinel" });

        await using var scope = app.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
        var raiser = scope.ServiceProvider.GetRequiredService<INotificationRaiser>();

        await using var transaction = await db.Database.BeginTransactionAsync();

        var record = new NotifiedRecord { Name = "Invoice" };
        db.Records.Add(record);
        await db.SaveChangesAsync();
        await raiser.RaiseAsync(Topics.Staff, record);

        // The queue is first in, first out. An event queued early would be handled, and lost, before this sentinel.
        await app.DrainAsync(sentinelRecord);
        Assert.Empty(app.Sent);

        await transaction.CommitAsync();
        await app.DrainAsync(sentinelRecord);

        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task PlainSaveChanges_RolledBack_SendsNothing()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var raiser = scope.ServiceProvider.GetRequiredService<INotificationRaiser>();

            await using var transaction = await db.Database.BeginTransactionAsync();

            var record = new NotifiedRecord { Name = "Invoice" };
            db.Records.Add(record);
            await db.SaveChangesAsync();
            await raiser.RaiseAsync(Topics.Staff, record);

            await transaction.RollbackAsync();
        }

        await app.DrainAsync();

        Assert.Empty(app.Sent);
    }

    [Fact]
    public async Task PlainSaveChanges_TransactionLeftWithoutACommit_SendsNothing()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        await using (var scope = app.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var raiser = scope.ServiceProvider.GetRequiredService<INotificationRaiser>();

            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                var record = new NotifiedRecord { Name = "Invoice" };
                db.Records.Add(record);
                await db.SaveChangesAsync();
                await raiser.RaiseAsync(Topics.Staff, record);

                // Left by an exception in real code: disposed with no commit and no rollback.
            }
        }

        await app.DrainAsync();

        Assert.Empty(app.Sent);
    }

    [Fact]
    public async Task TheSave_DoesNotWaitForThePipeline()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        app.Sender.BeforeSend = async _ =>
        {
            sending.SetResult();
            await release.Task;
        };

        var record = await app.SaveAsync();
        await app.RaiseAsync(Topics.Staff, record);

        // The raise has returned, and the sender is still waiting.
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(app.Sent);

        release.SetResult();
        await app.DrainAsync();

        Assert.Single(app.Sent);
    }

    [Fact]
    public async Task ARecordWithNoID_IsRefusedAndLogged_AndNeverThrows()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        await app.RaiseAsync(Topics.Staff, new NotifiedRecord { Name = "Never saved" });
        await app.DrainAsync();

        Assert.Empty(app.Sent);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("has no ID"));
    }

    [Fact]
    public async Task ATopicThatIsNotRegistered_IsRefusedAndLogged_AndNeverThrows()
    {
        await using var app = await NotificationTestApp.StartAsync();
        var unregistered = new NotificationTopic
        {
            ID = "Sales.Invoice.Unregistered",
            DisplayName = "Unregistered",
            Description = "Not in the catalog.",
            ReadPermission = NotificationTestActions.Invoices,
        };

        var record = await app.SaveAsync();
        await app.RaiseAsync(unregistered, record);
        await app.DrainAsync();

        Assert.Empty(app.Sent);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("isn't registered"));
    }

    [Fact]
    public async Task EmailShowsWhatWasCommitted()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        var record = await app.SaveAsync(new NotifiedRecord { Name = "Saved name" });
        record.Name = "Changed in memory, never saved";

        await app.RaiseAsync(Topics.Staff, record);
        await app.DrainAsync();

        Assert.Contains("Saved name", Assert.Single(app.Sent).TextBody);
    }

    // ── Users ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubscribedUserWhoPassesEveryCheck_GetsIt_InTo()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.SetEntries(UsersEntry(1));

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff);

        Assert.Equal(["user1@example.com"], Assert.Single(sent).To);
    }

    [Fact]
    public async Task SubscribedButWithoutTheReadPermission_GetsNothing_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1, canRead: false);
        app.SetEntries(UsersEntry(1));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("user 1 doesn't have the read permission"));
    }

    [Fact]
    public async Task SubscribedButCannotOpenTheRecord_GetsNothing_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1, canOpenRecord: false);
        app.SetEntries(UsersEntry(1));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("user 1 can't open"));
    }

    [Fact]
    public async Task UserWithAccessButNoSubscription_GetsNothing()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
    }

    [Fact]
    public async Task NotEligibleUser_GetsNothing_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.NotEligible[1] = "Inactive";
        app.SetEntries(UsersEntry(1));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("user 1 can't sign in (Inactive)"));
    }

    [Fact]
    public async Task UnresolvedUser_GetsNothing_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Unresolved[1] = "UserMissing: no document";
        app.SetEntries(UsersEntry(1));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("user 1 can't be worked out"));
    }

    [Fact]
    public async Task UnverifiedEmail_GetsNothing_WithTheSwitchOn()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1, emailVerified: false);
        app.SetEntries(UsersEntry(1));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("user 1 isn't verified"));
    }

    [Fact]
    public async Task UnverifiedEmail_GetsIt_WithTheSwitchOff()
    {
        await using var app = await NotificationTestApp.StartAsync(options => options.RequireVerifiedEmail = false);
        app.Directory.Users[1] = NotificationTestApp.User(1, emailVerified: false);
        app.SetEntries(UsersEntry(1));

        Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff));
    }

    [Fact]
    public async Task UserWithNoEmailAddress_GetsNothing_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        var user = NotificationTestApp.User(1) with { Email = null };
        app.Directory.Users[1] = user;
        app.SetEntries(UsersEntry(1));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("user 1 has no email address"));
    }

    [Fact]
    public async Task RepositoryWithAGlobalFilter_SendsToNoUsers_LogsAnError_AndAddressesStillGo()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Settings.RepositoryHasGlobalFilter = true;
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.SetEntries(new NotificationSubscribersEntryDTO
        {
            Topic = Topics.Staff.ID,
            Where = NotificationWhere.All(),
            Users = [UserHash(1)],
            Emails = new() { To = ["list@example.com"] },
        });

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff);

        Assert.Equal(["list@example.com"], Assert.Single(sent).To);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("no user gets it"));
    }

    [Fact]
    public async Task NoRepositoryRegistered_SendsToNoUsers_AndLogsAnError()
    {
        await using var app = await NotificationTestApp.StartAsync(services: services =>
            services.Remove(services.Single(d => d.ServiceType == typeof(Core.IShiftEntityFind<NotifiedRecord>))));

        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.SetEntries(UsersEntry(1));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("no user gets it"));
    }

    [Fact]
    public async Task NoIdentityDirectory_SendsToNoUsers_LogsAnError_AndAddressesStillGo()
    {
        await using var app = await NotificationTestApp.StartAsync(withDirectory: false);
        app.SetEntries(new NotificationSubscribersEntryDTO
        {
            Topic = Topics.Staff.ID,
            Where = NotificationWhere.All(),
            Users = [UserHash(1)],
            Emails = new() { To = ["list@example.com"] },
        });

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff);

        Assert.Equal(["list@example.com"], Assert.Single(sent).To);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains(nameof(INotificationIdentityDirectory)));
    }

    [Fact]
    public async Task Where_NarrowsWhatAUserHears()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.SetEntries(UsersEntry(1).With(e => e.Where = new NotificationWhere { Company = CompanyHash(99) }));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyID = 4 }));
    }

    [Fact]
    public async Task Where_NeverWidensAccess()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1, canOpenRecord: false);
        app.SetEntries(UsersEntry(1).With(e => e.Where = new NotificationWhere { Company = CompanyHash(4) }));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyID = 4 }));
    }

    [Fact]
    public async Task TeamMembers_ComeFromTheTeam_AndEachIsChecked()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Teams[11] = [1, 2];
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.Directory.Users[2] = NotificationTestApp.User(2, canRead: false);
        app.SetEntries(new NotificationSubscribersEntryDTO { Topic = Topics.Staff.ID, Teams = [TeamHash(11)] });

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff);

        Assert.Equal(["user1@example.com"], Assert.Single(sent).To);
    }

    [Fact]
    public async Task TeamWithUnknownMembers_GetsNothing_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Teams[11] = null;
        app.SetEntries(new NotificationSubscribersEntryDTO { Topic = Topics.Staff.ID, Teams = [TeamHash(11)] });

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("members of team 11 aren't known"));
    }

    [Fact]
    public async Task UserWatcher_IsChecked_LikeAnyUser()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.Directory.Users[2] = NotificationTestApp.User(2, canOpenRecord: false);

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord
        {
            TestWatchers = [NotificationWatcher.User(1, Topics.Staff), NotificationWatcher.User(2, Topics.Staff)],
        });

        Assert.Equal(["user1@example.com"], Assert.Single(sent).To);
    }

    [Fact]
    public async Task UserWatcher_OnAnotherTopic_GetsNothing()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord
        {
            TestWatchers = [NotificationWatcher.User(1, Topics.StaffUsersOnly)],
        }));
    }

    [Fact]
    public async Task WatchersThatThrow_AreLogged_AndTheListStillGetsIt()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { WatchersThrow = true });

        Assert.Equal(["list@example.com"], Assert.Single(sent).To);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("watchers"));
    }

    // ── Addresses ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Addresses_AreRefused_WhenTheTopicDoesNotAllowThem()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com").With(e => e.Topic = Topics.StaffUsersOnly.ID));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.StaffUsersOnly));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("doesn't allow emails"));
    }

    [Fact]
    public async Task Addresses_AreRefused_WhenWhereDoesNotMatch()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com", where: new NotificationWhere { Company = CompanyHash(99) }));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyID = 4 }));
    }

    [Fact]
    public async Task Addresses_Go_WhenWhereMatches()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com", where: new NotificationWhere { Company = CompanyHash(4) }));

        Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyID = 4 }));
    }

    [Fact]
    public async Task AddressesWithNoWhere_AreIgnored_EvenIfSavedSomehow()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com").With(e => e.Where = null));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("has addresses but no where"));
    }

    [Fact]
    public async Task BranchTags_AreReadFromTheRecordsOwnBranch()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Branches[8] =
        [
            new TaggedTextDTO { Text = "own@example.com", Tag = "Sales Team" },
            new TaggedTextDTO { Text = "spaced@example.com", Tag = "  sales team " },
            new TaggedTextDTO { Text = "other-tag@example.com", Tag = "Service Team" },
        ];
        app.Directory.Branches[9] = [new TaggedTextDTO { Text = "other-branch@example.com", Tag = "Sales Team" }];
        app.SetEntries(new NotificationSubscribersEntryDTO
        {
            Topic = Topics.Staff.ID,
            Where = NotificationWhere.All(),
            BranchEmails = new() { To = ["Sales Team"] },
        });

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyBranchID = 8 });

        Assert.Equal(["own@example.com", "spaced@example.com"], Assert.Single(sent).To);
        Assert.Equal([8], app.Directory.BranchesRead);
    }

    [Fact]
    public async Task BranchTags_GoToTheirOwnPlaces()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Branches[8] =
        [
            new TaggedTextDTO { Text = "to@example.com", Tag = "A" },
            new TaggedTextDTO { Text = "cc@example.com", Tag = "B" },
            new TaggedTextDTO { Text = "bcc@example.com", Tag = "C" },
        ];
        app.SetEntries(new NotificationSubscribersEntryDTO
        {
            Topic = Topics.Staff.ID,
            Where = NotificationWhere.All(),
            BranchEmails = new() { To = ["A"], Cc = ["B"], Bcc = ["C"] },
        });

        var email = Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyBranchID = 8 }));

        Assert.Equal(["to@example.com"], email.To);
        Assert.Equal(["cc@example.com"], email.Cc);
        Assert.Equal(["bcc@example.com"], email.Bcc);
    }

    [Fact]
    public async Task BranchTags_WithNoBranchOnTheRecord_AreSkipped_AndLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(new NotificationSubscribersEntryDTO
        {
            Topic = Topics.Staff.ID,
            Where = NotificationWhere.All(),
            BranchEmails = new() { To = ["Sales Team"] },
        });

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyBranchID = null }));
        Assert.Empty(app.Directory.BranchesRead);
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("has no branch"));
    }

    [Fact]
    public async Task EntryForAnUnknownTopic_IsIgnoredAndLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("gone@example.com").With(e => e.Topic = "Sales.Invoice.Retired"), EmailsEntry("list@example.com"));

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff);

        Assert.Equal(["list@example.com"], Assert.Single(sent).To);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("Sales.Invoice.Retired"));
    }

    // ── Merge ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EntriesAndWatchers_MergeIntoOneEmail()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.Directory.Users[2] = NotificationTestApp.User(2);
        app.SetEntries(
            EmailsEntry(to: "dealer@example.com", where: new NotificationWhere { Company = CompanyHash(4) }),
            EmailsEntry(cc: "manager@example.com"),
            UsersEntry(1).With(e => e.Where = new NotificationWhere { Company = CompanyHash(4), Branch = NotificationTestApp.Hash(NotificationIdentityHashIds.Branch, 8) }));

        var email = Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord
        {
            CompanyID = 4,
            CompanyBranchID = 8,
            TestWatchers = [NotificationWatcher.User(2, Topics.Staff)],
        }));

        Assert.Equal(["dealer@example.com", "user1@example.com", "user2@example.com"], email.To.Order());
        Assert.Equal(["manager@example.com"], email.Cc);
        Assert.Empty(email.Bcc);
    }

    [Fact]
    public async Task AnAddressListedTwice_KeepsItsStrongestPlace()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(
            EmailsEntry(bcc: "a@example.com", cc: "b@example.com", where: new NotificationWhere { Company = CompanyHash(4) }),
            EmailsEntry(to: "a@example.com", cc: "c@example.com", bcc: "b@example.com"));

        var email = Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyID = 4 }));

        Assert.Equal(["a@example.com"], email.To);
        Assert.Equal(["b@example.com", "c@example.com"], email.Cc);
        Assert.Empty(email.Bcc);
    }

    [Fact]
    public async Task DuplicatesThatDifferInCaseAndSpacing_Collapse()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1, email: "Shared@Example.com");
        app.SetEntries(
            UsersEntry(1),
            EmailsEntry(to: "  shared@example.COM "),
            EmailsEntry(cc: "SHARED@example.com", where: new NotificationWhere { Company = CompanyHash(4) }));

        var email = Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyID = 4 }));

        Assert.Single(email.To);
        Assert.Empty(email.Cc);
    }

    [Fact]
    public async Task NothingInTo_MeansNothingIsSent_AndLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry(cc: "manager@example.com"));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("nobody is left in To"));
    }

    [Fact]
    public async Task TwoTopics_MakeTwoEmails()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.SetEntries(UsersEntry(1), UsersEntry(1).With(e => e.Topic = Topics.StaffUsersOnly.ID));

        var record = await app.SaveAsync();
        await app.RaiseAsync(Topics.Staff, record);
        await app.RaiseAsync(Topics.StaffUsersOnly, record);
        await app.DrainAsync();

        Assert.Equal([Topics.Staff.ID, Topics.StaffUsersOnly.ID], app.Sent.Select(message => message.Subject));
    }

    [Fact]
    public async Task Except_RemovesThatUser()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);
        app.Directory.Users[2] = NotificationTestApp.User(2);
        app.Directory.Teams[11] = [2];
        app.SetEntries(new NotificationSubscribersEntryDTO { Topic = Topics.Staff.ID, Users = [UserHash(1)], Teams = [TeamHash(11)] });

        var email = Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord
        {
            TestWatchers = [NotificationWatcher.User(2, Topics.Staff)],
        }, except: [2]));

        Assert.Equal(["user1@example.com"], email.To);
    }

    [Fact]
    public async Task MalformedAddressFromAnySource_IsLeftOut_AndLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry(to: "good@example.com", cc: "not an address"));

        var email = Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff));

        Assert.Equal(["good@example.com"], email.To);
        Assert.Empty(email.Cc);
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("isn't valid"));
        Assert.DoesNotContain(app.Log.Lines, line => line.Message.Contains("not an address"));
    }

    // ── Customers ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CustomerTopic_GoesOnlyToCustomerWatchers_AsTheirOwnEmail()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Directory.Users[1] = NotificationTestApp.User(1);

        // Refused on save, so nothing like this is stored. It proves the send path ignores it too.
        app.SetEntries(EmailsEntry("staff@example.com").With(e => e.Topic = Topics.Customers.ID), UsersEntry(1).With(e => e.Topic = Topics.Customers.ID));

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Customers, new NotifiedRecord
        {
            TestWatchers =
            [
                NotificationWatcher.Customer("first@example.com", Topics.Customers),
                NotificationWatcher.Customer("second@example.com", Topics.Customers),
                NotificationWatcher.User(1, Topics.Customers),
            ],
        });

        Assert.Equal(2, sent.Count);
        Assert.Equal(["first@example.com"], sent[0].To);
        Assert.Equal(["second@example.com"], sent[1].To);
        Assert.All(sent, email => { Assert.Empty(email.Cc); Assert.Empty(email.Bcc); });
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("user watcher 1"));
    }

    [Fact]
    public async Task CustomerWatcher_OnAStaffTopic_IsIgnoredAndLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord
        {
            TestWatchers = [NotificationWatcher.Customer("customer@example.com", Topics.Staff)],
        }));

        Assert.Contains(app.Log.Lines, line => line.Message.Contains("customer watcher"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task CustomerWithNoAddress_GetsNothing(string? address)
    {
        await using var app = await NotificationTestApp.StartAsync();

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Customers, new NotifiedRecord
        {
            TestWatchers = [NotificationWatcher.Customer(address, Topics.Customers)],
        }));
    }

    [Fact]
    public async Task CustomerAddressThatHoldsTwoAddresses_IsRefused()
    {
        // The record's address is text a customer typed.
        await using var app = await NotificationTestApp.StartAsync();

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Customers, new NotifiedRecord
        {
            TestWatchers = [NotificationWatcher.Customer("a@example.com, b@example.com", Topics.Customers)],
        }));
    }

    [Fact]
    public async Task CustomerTopic_NeedsNoIdentityDirectory()
    {
        await using var app = await NotificationTestApp.StartAsync(withDirectory: false);

        Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Customers, new NotifiedRecord
        {
            TestWatchers = [NotificationWatcher.Customer("customer@example.com", Topics.Customers)],
        }));
    }

    [Fact]
    public async Task OneFailedCustomerSend_DoesNotStopTheOtherCustomer()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.Sender.BeforeSend = message => message.To[0].StartsWith("first")
            ? throw new InvalidOperationException("Deliberate sender failure.")
            : Task.CompletedTask;

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Customers, new NotifiedRecord
        {
            TestWatchers =
            [
                NotificationWatcher.Customer("first@example.com", Topics.Customers),
                NotificationWatcher.Customer("second@example.com", Topics.Customers),
            ],
        });

        Assert.Equal(["second@example.com"], Assert.Single(sent).To);
    }

    // ── Failures ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NullListsFromJson_ReadAsEmpty_AndDoNotBreakAnything()
    {
        var entry = System.Text.Json.JsonSerializer.Deserialize<NotificationSubscribersEntryDTO>(
            $$"""{ "topic": "{{Topics.Staff.ID}}", "where": "all", "users": null, "teams": null, "emails": { "to": ["list@example.com"], "cc": null, "bcc": null }, "branchEmails": null }""")!;

        Assert.Empty(entry.Users);
        Assert.Empty(entry.Teams);
        Assert.Empty(entry.Emails!.Cc);

        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(entry);

        Assert.Single(await app.SaveRaiseAndDrainAsync(Topics.Staff));
    }

    [Fact]
    public async Task TeamsWithNoIdentityDirectory_LogAnError()
    {
        await using var app = await NotificationTestApp.StartAsync(withDirectory: false);
        app.SetEntries(new NotificationSubscribersEntryDTO { Topic = Topics.Staff.ID, Teams = [TeamHash(11)] });

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("team 11"));
    }

    private sealed class FailingDirectory : INotificationIdentityDirectory
    {
        public Task<NotificationUserLookup> LookUpUsersAsync(IReadOnlyCollection<long> userIDs, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Deliberate Identity failure.");

        public Task<IReadOnlyList<long>?> GetTeamMembersAsync(long teamID, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Deliberate Identity failure.");

        public Task<IReadOnlyList<TaggedTextDTO>?> GetBranchEmailsAsync(long branchID, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Deliberate Identity failure.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedIdentityRead_SendsToNoUsers_LogsAnError_AndAddressesStillGo(bool throughTeam)
    {
        await using var app = await NotificationTestApp.StartAsync(services: services =>
            services.AddScoped<INotificationIdentityDirectory, FailingDirectory>());

        app.SetEntries(new NotificationSubscribersEntryDTO
        {
            Topic = Topics.Staff.ID,
            Where = NotificationWhere.All(),
            Users = throughTeam ? [] : [UserHash(1)],
            Teams = throughTeam ? [TeamHash(11)] : [],
            Emails = new() { To = ["list@example.com"] },
        });

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff);

        Assert.Equal(["list@example.com"], Assert.Single(sent).To);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("no user gets it"));
    }

    [Fact]
    public async Task AFailedBranchRead_SkipsTheTags_LogsAnError_AndOtherAddressesStillGo()
    {
        await using var app = await NotificationTestApp.StartAsync(services: services =>
            services.AddScoped<INotificationIdentityDirectory, FailingDirectory>());

        app.SetEntries(new NotificationSubscribersEntryDTO
        {
            Topic = Topics.Staff.ID,
            Where = NotificationWhere.All(),
            Emails = new() { To = ["list@example.com"] },
            BranchEmails = new() { To = ["Sales Team"] },
        });

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { CompanyBranchID = 8 });

        Assert.Equal(["list@example.com"], Assert.Single(sent).To);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("reading branch 8 failed"));
    }

    [Fact]
    public async Task ASenderThatHangs_DoesNotStopTheNextEvent_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync(options => options.EventTimeout = TimeSpan.FromMilliseconds(200));
        app.SetEntries(EmailsEntry("list@example.com"));

        // It ignores the token, as a stuck client would.
        var calls = 0;
        var never = new TaskCompletionSource();
        app.Sender.BeforeSend = _ => Interlocked.Increment(ref calls) == 1 ? never.Task : Task.CompletedTask;

        var record = await app.SaveAsync();
        await app.RaiseAsync(Topics.Staff, record);
        await app.RaiseAsync(Topics.Staff, record);
        await app.DrainAsync();

        Assert.Single(app.Sent);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("Notification failed"));

        never.SetResult();
    }

    // A second repository for the same type, open to everyone.
    public sealed class OpenRecordRepository(NotificationTestDbContext db) : ShiftSoftware.ShiftEntity.EFCore.ShiftRepository<NotificationTestDbContext, NotifiedRecord, NotifiedRecordDTO, NotifiedRecordDTO>(
        db, options => { options.UseMapper(new ThrowingNotifiedRecordMapper()); options.DataLevelAccess(access => access.Unscoped()); });

    [Fact]
    public async Task TwoRepositoriesForOneType_SendToNoUsers_RatherThanGuess()
    {
        await using var app = await NotificationTestApp.StartAsync(services: services =>
            services.AddScoped<Core.IShiftEntityFind<NotifiedRecord>, OpenRecordRepository>());

        app.Directory.Users[1] = NotificationTestApp.User(1, canOpenRecord: false);
        app.SetEntries(UsersEntry(1));

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("no user gets it"));
    }

    [Fact]
    public async Task UseRepository_NamesTheOneToCheck()
    {
        // The strict repository is named, so the open one is not asked.
        await using var strict = await NotificationTestApp.StartAsync(
            options => options.UseRepository<NotifiedRecord, NotifiedRecordRepository>(),
            services =>
            {
                services.AddScoped<NotifiedRecordRepository>();
                services.AddScoped<Core.IShiftEntityFind<NotifiedRecord>, OpenRecordRepository>();
            });

        strict.Directory.Users[1] = NotificationTestApp.User(1, canOpenRecord: false);
        strict.SetEntries(UsersEntry(1));

        Assert.Empty(await strict.SaveRaiseAndDrainAsync(Topics.Staff));
        Assert.DoesNotContain(strict.Log.Lines, line => line.Message.Contains("no user gets it"));
    }

    [Fact]
    public async Task AThrowingSender_DoesNotStopTheNextEvent_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        var failures = 0;
        app.Sender.BeforeSend = _ => Interlocked.Increment(ref failures) == 1
            ? throw new InvalidOperationException("Deliberate sender failure.")
            : Task.CompletedTask;

        var record = await app.SaveAsync();
        await app.RaiseAsync(Topics.Staff, record);
        await app.RaiseAsync(Topics.Staff, record);
        await app.DrainAsync();

        Assert.Single(app.Sent);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("Notification failed"));
    }

    private sealed class ThrowsOnceList(INotificationSubscribersList inner) : INotificationSubscribersList
    {
        private int reads;

        public Task<IReadOnlyList<NotificationSubscribersEntryDTO>> GetEntriesAsync(CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref reads) == 1
                ? throw new InvalidOperationException("Deliberate list failure.")
                : inner.GetEntriesAsync(cancellationToken);
    }

    [Fact]
    public async Task AThrowingRead_DoesNotStopTheNextEvent_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync(services: services =>
        {
            services.AddSingleton<INotificationSubscribersList>(sp => new ThrowsOnceList(sp.GetRequiredService<InMemoryNotificationSubscribersList>()));
        });
        app.SetEntries(EmailsEntry("list@example.com"));

        var record = await app.SaveAsync();
        await app.RaiseAsync(Topics.Staff, record);
        await app.RaiseAsync(Topics.Staff, record);
        await app.DrainAsync();

        Assert.Single(app.Sent);
        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("Notification failed"));
    }

    [Fact]
    public async Task NoContentProvider_SendsNothing_AndLogsAnError()
    {
        await using var app = await NotificationTestApp.StartAsync(withContent: false, services: services =>
            services.AddSingleton<INotificationSubscribersList>(new InMemoryNotificationSubscribersList()));

        // No content means even the sentinel can't be sent, so wait for the log line.
        var record = await app.SaveAsync();
        await app.RaiseAsync(Topics.Customers, new NotifiedRecord { ID = record.ID, TestWatchers = [NotificationWatcher.Customer("c@example.com", Topics.Customers)] });

        await WaitForLogAsync(app, "no INotificationEmailContent");

        Assert.Empty(app.Sender.Sent);
    }

    [Fact]
    public async Task ContentThatGivesNothing_SendsNothing_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        var record = await app.SaveAsync();
        app.Settings.ContentReturnsNull = true;
        await app.RaiseAsync(Topics.Staff, record);

        await WaitForLogAsync(app, "gave no email content");

        Assert.Empty(app.Sent);
    }

    [Fact]
    public async Task NoSender_SendsNothing_AndLogsAnError()
    {
        await using var app = await NotificationTestApp.StartAsync(withSender: false);
        app.SetEntries(EmailsEntry("list@example.com"));

        var record = await app.SaveAsync();
        await app.RaiseAsync(Topics.Staff, record);

        await WaitForLogAsync(app, nameof(INotificationEmailSender));
    }

    [Fact]
    public async Task ARecordDeletedBeforeItsEventIsHandled_SendsNothing_AndIsLogged()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        await app.RaiseAsync(Topics.Staff, new NotifiedRecord { ID = 123456 });
        await app.DrainAsync();

        Assert.Empty(app.Sent);
        Assert.Contains(app.Log.Lines, line => line.Message.Contains("no longer exists"));
    }

    private static async Task WaitForLogAsync(NotificationTestApp app, string text)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (!app.Log.Lines.Any(line => line.Message.Contains(text)))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"No log line with '{text}'. Log: " + string.Join(" | ", app.Log.Lines.Select(line => line.Message)));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // ── Language ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCultureOfTheRaise_IsTheOneRendered()
    {
        await using var app = await NotificationTestApp.StartAsync();
        app.SetEntries(EmailsEntry("list@example.com"));

        var record = await app.SaveAsync(new NotifiedRecord { Name = "Invoice" });

        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = new CultureInfo("ar-IQ");

            await app.RaiseAsync(Topics.Staff, record);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }

        await app.DrainAsync();

        var email = Assert.Single(app.Sent);

        Assert.Contains("Invoice|ar-IQ|fr-FR", email.TextBody);
        Assert.Contains("""dir="rtl" """.TrimEnd(), email.HtmlBody);
    }

    // ── Opt-in ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HostThatDoesNotOptIn_HasNoQueueAndNoBackgroundService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NotificationTestDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddControllers().AddShiftEntityWeb(o => o.AddDataAssembly(typeof(NotifiedRecord).Assembly));
        services.RegisterShiftRepositories(typeof(NotifiedRecord).Assembly);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "NotificationDispatcherService");
        Assert.DoesNotContain(services, d => d.ServiceType.Name == "NotificationQueue");
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(INotificationRaiser));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(INotificationSubscribersList));
    }

    [Fact]
    public void HostThatOptsIn_HasThem()
    {
        var services = new ServiceCollection();
        services.AddNotifications<NotificationTestDbContext>("Sales", options => options.AddTopics<Topics>());

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "NotificationDispatcherService");
        Assert.Contains(services, d => d.ServiceType.Name == "NotificationQueue");
        Assert.Contains(services, d => d.ServiceType == typeof(INotificationRaiser));
        Assert.Contains(services, d => d.ServiceType == typeof(INotificationSubscribersList));
    }

    [Fact]
    public void AListRegisteredBeforeOptingIn_IsKept()
    {
        var services = new ServiceCollection();
        var mine = new InMemoryNotificationSubscribersList();
        services.AddSingleton<INotificationSubscribersList>(mine);
        services.AddNotifications<NotificationTestDbContext>("Sales", options => options.AddTopics<Topics>());

        Assert.Same(mine, services.Single(d => d.ServiceType == typeof(INotificationSubscribersList)).ImplementationInstance);
    }
}
