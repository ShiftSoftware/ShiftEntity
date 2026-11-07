using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.Model.Notifications;
using System.Text;
using Xunit;
using Topics = ShiftSoftware.ShiftEntity.Tests.Notifications.NotificationTestTopics;

namespace ShiftSoftware.ShiftEntity.Tests.Notifications;

/// <summary>The subscribers list as a JSON file with one section per environment.</summary>
public class NotificationSubscribersJsonTests
{
    private static Stream Json(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static NotificationSubscribersEntryValidator Validator()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NotificationTestApp.HashIds);
        services.AddNotifications<NotificationTestDbContext>("Sales", options => options.AddTopics<Topics>());

        return services.BuildServiceProvider().GetRequiredService<NotificationSubscribersEntryValidator>();
    }

    private static string Entry(string to, string? topic = null, string where = "\"all\"")
        => $$"""{ "topic": "{{topic ?? Topics.Staff.ID}}", "where": {{where}}, "emails": { "to": ["{{to}}"] } }""";

    private static NotificationSubscribersJsonSection Read(string json, string? section = "Production")
        => NotificationSubscribersJson.ReadSection(Json(json), section, Validator());

    // ── Reading a section ───────────────────────────────────────────────────────

    [Fact]
    public void TheRightSection_Loads()
    {
        var result = Read($$"""{ "Development": [{{Entry("dev@example.com")}}], "Production": [{{Entry("prod@example.com")}}] }""");

        Assert.True(result.Found);
        Assert.Empty(result.Errors);
        Assert.Equal(["prod@example.com"], Assert.Single(result.Entries).Emails!.To);
    }

    [Fact]
    public void TheWholeEntryShape_Loads()
    {
        var company = NotificationTestApp.Hash(NotificationIdentityHashIds.Company, 4);
        var user = NotificationTestApp.Hash(NotificationIdentityHashIds.User, 1);
        var team = NotificationTestApp.Hash(NotificationIdentityHashIds.Team, 2);

        var result = Read($$"""
            {
              "Production": [
                {
                  "topic": "{{Topics.Staff.ID}}",
                  "where": { "company": "{{company}}" },
                  "users": ["{{user}}"],
                  "teams": ["{{team}}"],
                  "emails": { "to": ["to@example.com"], "cc": ["cc@example.com"], "bcc": ["bcc@example.com"] },
                  "branchEmails": { "to": ["Sales Team"] }
                }
              ]
            }
            """);

        Assert.Empty(result.Errors);

        var entry = Assert.Single(result.Entries);

        Assert.Equal(company, entry.Where!.Company);
        Assert.Equal([user], entry.Users);
        Assert.Equal([team], entry.Teams);
        Assert.Equal(["cc@example.com"], entry.Emails!.Cc);
        Assert.Equal(["Sales Team"], entry.BranchEmails!.To);
    }

    [Fact]
    public void TheSectionName_IgnoresCase()
    {
        Assert.Single(Read($$"""{ "production": [{{Entry("prod@example.com")}}] }""", "PRODUCTION").Entries);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreFine_BecausePeopleEditThisFile()
    {
        var result = Read($$"""
            {
              // Who hears about new invoices.
              "Production": [ {{Entry("prod@example.com")}}, ],
            }
            """);

        Assert.Single(result.Entries);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData(null)]
    [InlineData("")]
    public void AnEnvironmentWithNoSection_GetsAnEmptyList(string? section)
    {
        var result = Read($$"""{ "Production": [{{Entry("prod@example.com")}}] }""", section);

        Assert.False(result.Found);
        Assert.Empty(result.Entries);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AnEmptySection_IsFound_AndEmpty()
    {
        var result = Read("""{ "Production": [] }""");

        Assert.True(result.Found);
        Assert.Empty(result.Entries);
        Assert.Empty(result.Errors);
    }

    // ── Invalid entries ─────────────────────────────────────────────────────────

    public static TheoryData<string, string> InvalidEntries => new()
    {
        { Entry("not an address"), "not a valid email address" },
        { Entry("a@example.com", topic: "Sales.Invoice.Gone"), "not a topic" },
        { Entry("a@example.com", topic: "Stock.Invoice.Created"), "not a topic" },
        { Entry("a@example.com", topic: Topics.Customers.ID), "for customers" },
        { Entry("a@example.com", topic: Topics.StaffUsersOnly.ID), "doesn't allow email addresses" },
        { Entry("a@example.com", where: """{ "colour": "red" }"""), "colour" },
        { Entry("a@example.com", where: """{ "company": "not a hash!" }"""), "not a valid hash ID" },
        { Entry("a@example.com", where: """{}"""), "empty" },
        { Entry("a@example.com", where: "null"), "must have a where" },
        { """{ "topic": "Sales.Invoice.Created", "where": "all" }""", "no users, teams, emails or branch emails" },
        { """{ "topic": "Sales.Invoice.Created", "where": "all", "emailz": { "to": ["a@example.com"] } }""", "emailz" },
        { """{ "Topic": "Sales.Invoice.Created", "where": "all", "emails": { "to": ["a@example.com"] } }""", "Topic" },
        { """{ "topic": "Sales.Invoice.Created", "where": "all", "emails": { "too": ["a@example.com"] } }""", "too" },
        { """{ "topic": "Sales.Invoice.Created", "where": "all", "users": 5 }""", "users" },
        { "null", "empty" },
        { "5", "entry 1" },
    };

    [Theory]
    [MemberData(nameof(InvalidEntries))]
    public void AnInvalidEntry_IsDropped_AndTheValidOnesStillLoad(string invalid, string errorPart)
    {
        var result = Read($$"""{ "Production": [ {{Entry("first@example.com")}}, {{invalid}} ] }""");

        Assert.Equal(["first@example.com"], Assert.Single(result.Entries).Emails!.To);
        Assert.Contains(result.Errors, error => error.Contains(errorPart) && error.Contains("Production"));
    }

    [Fact]
    public void ASecondEntryWithTheSameTopicAndWhere_IsDropped()
    {
        var result = Read($$"""{ "Production": [ {{Entry("first@example.com")}}, {{Entry("second@example.com")}} ] }""");

        Assert.Equal(["first@example.com"], Assert.Single(result.Entries).Emails!.To);
        Assert.Contains(result.Errors, error => error.Contains("already has an entry"));
    }

    [Fact]
    public void TheSameTopicWithAnotherWhere_Loads()
    {
        var company = NotificationTestApp.Hash(NotificationIdentityHashIds.Company, 4);

        var result = Read($$"""{ "Production": [ {{Entry("first@example.com")}}, {{Entry("second@example.com", where: $$"""{ "company": "{{company}}" }""")}} ] }""");

        Assert.Equal(2, result.Entries.Count);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "Production": { "topic": "x" } }""")]
    public void AFileThatCanNotBeUsed_GivesAnEmptyList_AndAnError(string json)
    {
        var result = Read(json);

        Assert.Empty(result.Entries);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void TheSections_CanBeListed_SoAHostsTestCanCheckThemAll()
    {
        Assert.Equal(["Development", "Production"], NotificationSubscribersJson.GetSections(Json("""{ "Development": [], "Production": [] }""")));
    }

    // ── In a running host ───────────────────────────────────────────────────────

    private static Task<NotificationTestApp> StartAsync(string? json, string? section = "Production", Func<Stream?>? open = null)
        => NotificationTestApp.StartAsync(services: services => services.AddNotificationSubscribersJson(open ?? (() => Json(json!)), section));

    [Fact]
    public async Task TheSendPath_ReadsTheFile()
    {
        await using var app = await StartAsync($$"""
            {
              "Production": [
                {{Entry("stored@example.com")}},
                {{Entry("sentinel@example.com", topic: NotificationTestApp.SentinelTopic.ID)}}
              ],
              "Development": [ {{Entry("wrong-environment@example.com")}} ]
            }
            """);

        var sent = await app.SaveRaiseAndDrainAsync(Topics.Staff);

        Assert.Equal(["stored@example.com"], Assert.Single(sent).To);
    }

    [Fact]
    public async Task AnInvalidEntry_IsLoggedAsAnError_WhenTheHostStarts_AndTheHostStillStarts()
    {
        await using var app = await StartAsync($$"""{ "Production": [ {{Entry("not an address")}}, {{Entry("good@example.com")}} ] }""");

        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("not a valid email address"));

        var entries = await app.Provider.GetRequiredService<INotificationSubscribersList>().GetEntriesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["good@example.com"], Assert.Single(entries).Emails!.To);
    }

    [Fact]
    public async Task AMissingSection_IsAWarning_WhenTheHostStarts_AndTheListIsEmpty()
    {
        await using var app = await StartAsync($$"""{ "Production": [ {{Entry("prod@example.com")}} ] }""", "Staging");

        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("no section 'Staging'"));
        Assert.DoesNotContain(app.Log.Lines, line => line.Level == LogLevel.Error);
        Assert.Empty(await app.Provider.GetRequiredService<INotificationSubscribersList>().GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoEnvironmentName_IsAWarning_AndTheListIsEmpty()
    {
        // A host that can't tell where it runs must not send a production list.
        await using var app = await StartAsync($$"""{ "Production": [ {{Entry("prod@example.com")}} ] }""", section: null);

        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("no section"));
        Assert.Empty(await app.Provider.GetRequiredService<INotificationSubscribersList>().GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public async Task AnUnusableFile_IsAnError_AndTheHostStillStarts(string json)
    {
        await using var app = await StartAsync(json);

        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error);
        Assert.Empty(await app.Provider.GetRequiredService<INotificationSubscribersList>().GetEntriesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMissingFile_IsAnError_AndTheHostStillStarts()
    {
        await using var app = await StartAsync(null, open: () => null);

        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("wasn't found"));
    }

    [Fact]
    public async Task AFileThatThrowsWhenOpened_IsAnError_AndTheHostStillStarts()
    {
        await using var app = await StartAsync(null, open: () => throw new IOException("Deliberate failure."));

        Assert.Contains(app.Log.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("couldn't be read"));
    }

    [Fact]
    public void TheFile_ReplacesTheInMemoryList_InEitherOrder()
    {
        var after = new ServiceCollection();
        after.AddNotifications<NotificationTestDbContext>("Sales", options => options.AddTopics<Topics>());
        after.AddNotificationSubscribersJson(() => null, "Production");

        var before = new ServiceCollection();
        before.AddNotificationSubscribersJson(() => null, "Production");
        before.AddNotifications<NotificationTestDbContext>("Sales", options => options.AddTopics<Topics>());

        foreach (var services in new[] { after, before })
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == typeof(INotificationSubscribersList));

            Assert.NotNull(descriptor.ImplementationFactory);
            Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "NotificationSubscribersJsonLoader");
        }
    }

    [Fact]
    public void AHostThatDoesNotOptIn_HasNoLoader()
    {
        var services = new ServiceCollection();
        services.AddNotifications<NotificationTestDbContext>("Sales", options => options.AddTopics<Topics>());

        Assert.DoesNotContain(services, d => d.ImplementationType?.Name == "NotificationSubscribersJsonLoader");
    }
}
