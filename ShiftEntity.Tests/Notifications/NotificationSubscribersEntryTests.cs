using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.ShiftEntity.Model.Flags;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Model.Notifications;
using System.Text.Json;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Notifications;

/// <summary>A record that declares every marker a <c>where</c> can read.</summary>
public class MarkedRecord : ShiftEntity<MarkedRecord>,
    IEntityHasCompany<MarkedRecord>,
    IEntityHasCompanyBranch<MarkedRecord>,
    IEntityHasBrand<MarkedRecord>,
    IEntityHasRegion<MarkedRecord>,
    IEntityHasCity<MarkedRecord>,
    IEntityHasCountry<MarkedRecord>,
    IEntityHasTeam<MarkedRecord>
{
    public long? CompanyID { get; set; }
    public long? CompanyBranchID { get; set; }
    public long? BrandID { get; set; }
    public long? RegionID { get; set; }
    public long? CityID { get; set; }
    public long? CountryID { get; set; }
    public long? TeamID { get; set; }
}

/// <summary>A record that declares no marker.</summary>
public class UnmarkedRecord : ShiftEntity<UnmarkedRecord>;

public class EntryTopics
{
    public static readonly NotificationTopic WithEmails = new()
    {
        ID = "Sales.Invoice.Created",
        DisplayName = "Invoice created",
        Description = "A new invoice was saved.",
        ReadPermission = NotificationTestActions.Invoices,
        AllowEmails = true,
    };

    public static readonly NotificationTopic WithoutEmails = new()
    {
        ID = "Sales.Invoice.Approved",
        DisplayName = "Invoice approved",
        Description = "An invoice was approved.",
        ReadPermission = NotificationTestActions.Invoices,
    };

    public static readonly NotificationTopic ForCustomers = new()
    {
        ID = "Sales.Invoice.Paid",
        DisplayName = "Invoice paid",
        Description = "Tells the customer that the invoice was paid.",
        ForCustomers = true,
    };
}

/// <summary>The subscribers entry: its JSON shape, its validation and the <c>where</c> matcher.</summary>
public class NotificationSubscribersEntryTests
{
    private static readonly IHashIdService hashIds = BuildHashIds();

    private static IHashIdService BuildHashIds()
    {
        var options = new ShiftEntityOptions();
        options.HashId.RegisterIdentityHashId("a test salt", minHashLength: 5);
        return new HashIdService(Options.Create(options));
    }

    private static string Hash(JsonHashIdConverterAttribute kind, long id) => hashIds.Encode(id, kind);

    private static NotificationSubscribersEntryValidator Validator()
    {
        var services = new ServiceCollection();
        services.AddSingleton(hashIds);
        services.AddNotifications<NotificationTestDbContext>("Sales", o => o.AddTopics<EntryTopics>());

        using var provider = services.BuildServiceProvider();
        return new NotificationSubscribersEntryValidator(provider.GetRequiredService<NotificationTopicCatalog>(), hashIds);
    }

    private static readonly NotificationWhereMatcher matcher = new(hashIds);

    private static NotificationRecipients To(params string[] to) => new() { To = [.. to] };

    // ── where: each key ─────────────────────────────────────────────────────────

    public static TheoryData<string> Keys => ["company", "branch", "brand", "region", "city", "country", "team"];

    private static (NotificationWhere Where, Action<MarkedRecord, long?> Set) KeyOn(string key, long id) => key switch
    {
        "company" => (new() { Company = Hash(NotificationIdentityHashIds.Company, id) }, (r, v) => r.CompanyID = v),
        "branch" => (new() { Branch = Hash(NotificationIdentityHashIds.Branch, id) }, (r, v) => r.CompanyBranchID = v),
        "brand" => (new() { Brand = Hash(NotificationIdentityHashIds.Brand, id) }, (r, v) => r.BrandID = v),
        "region" => (new() { Region = Hash(NotificationIdentityHashIds.Region, id) }, (r, v) => r.RegionID = v),
        "city" => (new() { City = Hash(NotificationIdentityHashIds.City, id) }, (r, v) => r.CityID = v),
        "country" => (new() { Country = Hash(NotificationIdentityHashIds.Country, id) }, (r, v) => r.CountryID = v),
        "team" => (new() { Team = Hash(NotificationIdentityHashIds.Team, id) }, (r, v) => r.TeamID = v),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void Key_MatchesARecordWithTheSameValue(string key)
    {
        var (where, set) = KeyOn(key, 7);
        var record = new MarkedRecord();
        set(record, 7);

        Assert.True(matcher.Matches(where, record));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Key_DoesNotMatchARecordWithAnotherValue(string key)
    {
        var (where, set) = KeyOn(key, 7);
        var record = new MarkedRecord();
        set(record, 8);

        Assert.False(matcher.Matches(where, record));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Key_DoesNotMatchARecordWithNoValue(string key)
    {
        var (where, _) = KeyOn(key, 7);

        Assert.False(matcher.Matches(where, new MarkedRecord()));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Key_DoesNotMatchARecordWithoutTheMarker(string key)
    {
        var (where, _) = KeyOn(key, 7);

        Assert.False(matcher.Matches(where, new UnmarkedRecord()));
    }

    [Fact]
    public void EveryKeyGiven_MustMatch()
    {
        var where = new NotificationWhere
        {
            Company = Hash(NotificationIdentityHashIds.Company, 1),
            Brand = Hash(NotificationIdentityHashIds.Brand, 2),
        };

        Assert.True(matcher.Matches(where, new MarkedRecord { CompanyID = 1, BrandID = 2 }));
        Assert.False(matcher.Matches(where, new MarkedRecord { CompanyID = 1, BrandID = 3 }));
        Assert.False(matcher.Matches(where, new MarkedRecord { CompanyID = 1 }));
    }

    [Fact]
    public void All_MatchesEveryRecord_WithOrWithoutMarkers()
    {
        Assert.True(matcher.Matches(NotificationWhere.All(), new MarkedRecord()));
        Assert.True(matcher.Matches(NotificationWhere.All(), new UnmarkedRecord()));
    }

    [Fact]
    public void WhereThatIsWrong_NeverMatches()
    {
        var record = new MarkedRecord { CompanyID = 7 };

        Assert.False(matcher.Matches(new NotificationWhere(), record));                                     // empty
        Assert.False(matcher.Matches(new NotificationWhere { Company = "not a hash!" }, record));           // bad hash
        Assert.False(matcher.Matches(Read("""{ "company": "x", "colour": "red" }"""), record));             // unknown key
        Assert.False(matcher.Matches(Read("\"everything\""), record));                                      // unknown text
        Assert.False(matcher.Matches(new NotificationWhere
        {
            IsAll = true,
            Company = Hash(NotificationIdentityHashIds.Company, 7),
        }, record));                                                                                        // both
    }

    // ── JSON shape ──────────────────────────────────────────────────────────────

    private static NotificationWhere Read(string json) => JsonSerializer.Deserialize<NotificationWhere>(json)!;

    [Fact]
    public void Json_ReadsAndWritesTheDocumentedShape()
    {
        var company = Hash(NotificationIdentityHashIds.Company, 1);
        var user = Hash(NotificationIdentityHashIds.User, 2);

        var json = $$"""
        {
            "topic": "Sales.Invoice.Created",
            "where": { "company": "{{company}}" },
            "users": ["{{user}}"],
            "teams": [],
            "emails": { "to": ["a@example.com"], "cc": ["b@example.com"], "bcc": ["c@example.com"] },
            "branchEmails": { "to": ["Sales Team"] }
        }
        """;

        var entry = JsonSerializer.Deserialize<NotificationSubscribersEntryDTO>(json)!;

        Assert.Equal("Sales.Invoice.Created", entry.Topic);
        Assert.Equal(company, entry.Where!.Company);
        Assert.Equal([user], entry.Users);
        Assert.Equal(["a@example.com"], entry.Emails!.To);
        Assert.Equal(["b@example.com"], entry.Emails.Cc);
        Assert.Equal(["c@example.com"], entry.Emails.Bcc);
        Assert.Equal(["Sales Team"], entry.BranchEmails!.To);

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(entry));
        Assert.Equal(company, written.RootElement.GetProperty("where").GetProperty("company").GetString());
        Assert.Equal("a@example.com", written.RootElement.GetProperty("emails").GetProperty("to")[0].GetString());
    }

    [Fact]
    public void Json_AllIsText_BothWays()
    {
        Assert.True(Read("\"all\"").IsAll);
        Assert.Equal("\"all\"", JsonSerializer.Serialize(NotificationWhere.All()));
    }

    [Theory]
    [InlineData("""{ "colour": "red" }""", "colour")]
    [InlineData("""{ "company": 12 }""", "company")]
    [InlineData("""{ "company": { "id": "x" } }""", "company")]
    [InlineData("\"everything\"", "everything")]
    public void Json_KeepsWhatItDoesNotKnow_SoSavingCanRefuseIt(string json, string part)
    {
        var where = Read(json);

        Assert.Contains(where.UnreadableParts, p => p.Contains(part));
        Assert.False(where.HasKeys);
    }

    [Fact]
    public void Json_NullWhere_MeansLeftOut()
    {
        var entry = JsonSerializer.Deserialize<NotificationSubscribersEntryDTO>("""{ "topic": "x", "where": null }""")!;

        Assert.Null(entry.Where);
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    private static void AssertRefused(NotificationSubscribersEntryDTO entry, string field, string messagePart)
    {
        var errors = Validator().Validate(entry);

        Assert.Contains(errors, e => e.Field == field && e.Message.Contains(messagePart));
    }

    private static void AssertAccepted(NotificationSubscribersEntryDTO entry)
    {
        Assert.Empty(Validator().Validate(entry));
    }

    private static NotificationSubscribersEntryDTO EmailsEntry(NotificationWhere? where = null) => new()
    {
        Topic = EntryTopics.WithEmails.ID,
        Where = where ?? NotificationWhere.All(),
        Emails = To("a@example.com"),
    };

    [Fact]
    public void EmailOnlyEntry_IsAccepted()
    {
        AssertAccepted(EmailsEntry());
    }

    [Fact]
    public void BranchEmailOnlyEntry_IsAccepted()
    {
        AssertAccepted(new()
        {
            Topic = EntryTopics.WithEmails.ID,
            Where = NotificationWhere.All(),
            BranchEmails = To("Sales Team"),
        });
    }

    [Fact]
    public void EntryWithCcOnly_IsAccepted()
    {
        // One entry can copy someone on every record while other entries hold the To.
        AssertAccepted(new()
        {
            Topic = EntryTopics.WithEmails.ID,
            Where = NotificationWhere.All(),
            Emails = new() { Cc = ["manager@example.com"] },
        });
    }

    [Fact]
    public void EntryWithNoPhoneAndNoEmail_IsAccepted_AndUsersOnlyMayLeaveOutWhere()
    {
        // The topic doesn't allow emails, and users and teams don't need it to.
        AssertAccepted(new()
        {
            Topic = EntryTopics.WithoutEmails.ID,
            Users = [Hash(NotificationIdentityHashIds.User, 1)],
            Teams = [Hash(NotificationIdentityHashIds.Team, 2)],
        });
    }

    [Fact]
    public void EmailsWithoutWhere_AreRefused()
    {
        AssertRefused(new() { Topic = EntryTopics.WithEmails.ID, Emails = To("a@example.com") },
            nameof(NotificationSubscribersEntryDTO.Where), "must have a where");

        AssertRefused(new() { Topic = EntryTopics.WithEmails.ID, BranchEmails = To("Sales Team") },
            nameof(NotificationSubscribersEntryDTO.Where), "must have a where");
    }

    [Fact]
    public void EmailsWithWhereAll_AreAccepted()
    {
        AssertAccepted(EmailsEntry(NotificationWhere.All()));
    }

    [Fact]
    public void WhereWithHashIDs_IsAccepted()
    {
        AssertAccepted(EmailsEntry(new NotificationWhere
        {
            Company = Hash(NotificationIdentityHashIds.Company, 1),
            Branch = Hash(NotificationIdentityHashIds.Branch, 2),
            Brand = Hash(NotificationIdentityHashIds.Brand, 3),
            Region = Hash(NotificationIdentityHashIds.Region, 4),
            City = Hash(NotificationIdentityHashIds.City, 5),
            Country = Hash(NotificationIdentityHashIds.Country, 6),
            Team = Hash(NotificationIdentityHashIds.Team, 7),
        }));
    }

    [Theory]
    [InlineData("""{ "colour": "red" }""", "colour")]
    [InlineData("\"everything\"", "everything")]
    [InlineData("""{ "company": 12 }""", "company")]
    public void BadWhereFromJson_IsRefused(string json, string messagePart)
    {
        AssertRefused(EmailsEntry(Read(json)), nameof(NotificationSubscribersEntryDTO.Where), messagePart);
    }

    [Fact]
    public void EmptyWhere_IsRefused()
    {
        AssertRefused(EmailsEntry(new NotificationWhere()), nameof(NotificationSubscribersEntryDTO.Where), "empty");
    }

    [Fact]
    public void WhereWithAllAndKeys_IsRefused()
    {
        AssertRefused(EmailsEntry(new NotificationWhere { IsAll = true, Company = Hash(NotificationIdentityHashIds.Company, 1) }),
            nameof(NotificationSubscribersEntryDTO.Where), "not both");
    }

    [Theory]
    [InlineData("not a hash!")]
    [InlineData("")]
    public void BadHashInWhere_IsRefused(string hash)
    {
        AssertRefused(EmailsEntry(new NotificationWhere { Company = hash }), nameof(NotificationSubscribersEntryDTO.Where), "not a valid hash ID");
    }

    [Fact]
    public void BadHashForUserOrTeam_IsRefused()
    {
        AssertRefused(new() { Topic = EntryTopics.WithEmails.ID, Users = ["not a hash!"] },
            nameof(NotificationSubscribersEntryDTO.Users), "not a valid hash ID");

        AssertRefused(new() { Topic = EntryTopics.WithEmails.ID, Teams = ["not a hash!"] },
            nameof(NotificationSubscribersEntryDTO.Teams), "not a valid hash ID");
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("a@")]
    [InlineData("Someone <a@example.com>")]
    [InlineData("a@example.com, b@example.com")]
    [InlineData(" ")]
    public void BadAddress_IsRefused_InEachPlace(string address)
    {
        var field = nameof(NotificationSubscribersEntryDTO.Emails);

        AssertRefused(EmailsEntry().With(e => e.Emails = To(address)), field, "not a valid email address");
        AssertRefused(EmailsEntry().With(e => e.Emails = new() { Cc = [address] }), field, "not a valid email address");
        AssertRefused(EmailsEntry().With(e => e.Emails = new() { Bcc = [address] }), field, "not a valid email address");
    }

    [Fact]
    public void AddressWithSpaceAround_IsAccepted_BecauseSendingTrimsIt()
    {
        AssertAccepted(EmailsEntry().With(e => e.Emails = To("  a@example.com ")));
    }

    [Fact]
    public void EmptyBranchTag_IsRefused()
    {
        AssertRefused(EmailsEntry().With(e => { e.Emails = null; e.BranchEmails = To(" "); }),
            nameof(NotificationSubscribersEntryDTO.BranchEmails), "can't be empty");
    }

    [Fact]
    public void UnknownTopic_IsRefused()
    {
        AssertRefused(EmailsEntry().With(e => e.Topic = "Sales.Invoice.Deleted"),
            nameof(NotificationSubscribersEntryDTO.Topic), "not a topic");
    }

    [Fact]
    public void AddressesOnATopicThatDoesNotAllowThem_AreRefused()
    {
        AssertRefused(EmailsEntry().With(e => e.Topic = EntryTopics.WithoutEmails.ID),
            nameof(NotificationSubscribersEntryDTO.Emails), "doesn't allow email addresses");

        AssertRefused(EmailsEntry().With(e => { e.Topic = EntryTopics.WithoutEmails.ID; e.Emails = null; e.BranchEmails = To("Sales Team"); }),
            nameof(NotificationSubscribersEntryDTO.Emails), "doesn't allow email addresses");
    }

    [Fact]
    public void TopicForCustomers_RefusesEntries()
    {
        AssertRefused(new() { Topic = EntryTopics.ForCustomers.ID, Users = [Hash(NotificationIdentityHashIds.User, 1)] },
            nameof(NotificationSubscribersEntryDTO.Topic), "for customers");

        AssertRefused(EmailsEntry().With(e => e.Topic = EntryTopics.ForCustomers.ID),
            nameof(NotificationSubscribersEntryDTO.Topic), "for customers");
    }

    [Fact]
    public void EntryWithNoRecipients_IsRefused()
    {
        AssertRefused(new() { Topic = EntryTopics.WithEmails.ID, Where = NotificationWhere.All() },
            nameof(NotificationSubscribersEntryDTO.Users), "no users, teams, emails or branch emails");
    }

    [Fact]
    public void SecondEntryWithTheSameTopicAndWhere_IsRefused()
    {
        var company = Hash(NotificationIdentityHashIds.Company, 1);
        var saved = EmailsEntry(new NotificationWhere { Company = company });

        var errors = Validator().Validate(EmailsEntry(new NotificationWhere { Company = company }), [saved]);

        Assert.Contains(errors, e => e.Field == nameof(NotificationSubscribersEntryDTO.Where) && e.Message.Contains("already has an entry"));
    }

    [Fact]
    public void LeftOutWhere_CountsAsAll_WhenLookingForASecondEntry()
    {
        var user = Hash(NotificationIdentityHashIds.User, 1);

        var errors = Validator().Validate(
            new() { Topic = EntryTopics.WithEmails.ID, Users = [user] },
            [EmailsEntry(NotificationWhere.All())]);

        Assert.Contains(errors, e => e.Message.Contains("already has an entry"));
    }

    [Fact]
    public void SameTopicWithAnotherWhere_IsAccepted()
    {
        var saved = EmailsEntry(new NotificationWhere { Company = Hash(NotificationIdentityHashIds.Company, 1) });

        Assert.Empty(Validator().Validate(EmailsEntry(new NotificationWhere { Company = Hash(NotificationIdentityHashIds.Company, 2) }), [saved]));
        Assert.Empty(Validator().Validate(EmailsEntry(NotificationWhere.All()), [saved]));
    }

    [Fact]
    public void AnotherTopicWithTheSameWhere_IsAccepted()
    {
        var saved = EmailsEntry().With(e => e.Topic = "Sales.Invoice.Other");

        Assert.Empty(Validator().Validate(EmailsEntry(), [saved]));
    }

    [Fact]
    public void HashIDsWithoutHashSettings_AreNumbers()
    {
        // A host that turned hash IDs off has plain numbers. Other text is still refused.
        IHashIdService plain = new HashIdService(Options.Create(new ShiftEntityOptions()));

        Assert.True(plain.TryDecode("12", NotificationIdentityHashIds.Company, out var id));
        Assert.Equal(12, id);
        Assert.False(plain.TryDecode("abc", NotificationIdentityHashIds.Company, out _));
        Assert.False(plain.TryDecode("99999999999999999999", NotificationIdentityHashIds.Company, out _));
    }
}

internal static class NotificationEntryTestExtensions
{
    public static NotificationSubscribersEntryDTO With(this NotificationSubscribersEntryDTO entry, Action<NotificationSubscribersEntryDTO> change)
    {
        change(entry);
        return entry;
    }
}
