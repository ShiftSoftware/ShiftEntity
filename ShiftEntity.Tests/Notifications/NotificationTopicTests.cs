using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Notifications;

[ActionTree("Notification Test Actions", "Static actions for the notification tests.")]
public class NotificationTestActions
{
    public readonly static ReadWriteDeleteAction Invoices = new("Invoices");
}

/// <summary>Topics as a host declares them: public static fields, some in a nested class.</summary>
public class SalesTopics
{
    public static readonly NotificationTopic InvoiceCreated = new()
    {
        ID = "Sales.Invoice.Created",
        DisplayName = "Invoice created",
        Description = "A new invoice was saved.",
        ReadPermission = NotificationTestActions.Invoices,
        AllowEmails = true,
    };

    public static class Customers
    {
        public static readonly NotificationTopic InvoicePaid = new()
        {
            ID = "Sales.Invoice.Paid",
            DisplayName = "Invoice paid",
            Description = "Tells the customer that the invoice was paid.",
            ForCustomers = true,
        };
    }

    // Not topics: the scan must skip all of these.
    public const string NotATopic = "Sales.Invoice.Ignored";
    public static readonly string AlsoNotATopic = "Sales.Invoice.Ignored";
    private static readonly NotificationTopic Hidden = Staff("Sales.Invoice.Hidden");
    public static NotificationTopic Unused() => Hidden;

    internal static NotificationTopic Staff(string id, string displayName = "A topic") => new()
    {
        ID = id,
        DisplayName = displayName,
        Description = "A description.",
        ReadPermission = NotificationTestActions.Invoices,
    };
}

public class NoTopics
{
    public static readonly string NotATopic = "Sales.Invoice.Ignored";
}

public class DuplicateTopics
{
    public static readonly NotificationTopic First = SalesTopics.Staff("Sales.Invoice.Created");
    public static readonly NotificationTopic Second = SalesTopics.Staff("Sales.Invoice.Created");
}

public class DuplicateTopicsInAnotherCase
{
    public static readonly NotificationTopic First = SalesTopics.Staff("Sales.Invoice.Created");
    public static readonly NotificationTopic Second = SalesTopics.Staff("Sales.Invoice.created");
}

public class TopicsFromAnotherSystem
{
    public static readonly NotificationTopic Topic = SalesTopics.Staff("Stock.Product.Created");
}

public class TopicsWithALongerSystemName
{
    public static readonly NotificationTopic Topic = SalesTopics.Staff("SalesExtra.Invoice.Created");
}

public class TopicsWithSystemNameInAnotherCase
{
    public static readonly NotificationTopic Topic = SalesTopics.Staff("sales.Invoice.Created");
}

public class TopicsWithNothingAfterTheSystemName
{
    public static readonly NotificationTopic Topic = SalesTopics.Staff("Sales.");
}

public class TopicsWithASpaceInTheID
{
    public static readonly NotificationTopic Topic = SalesTopics.Staff("Sales.Invoice Created");
}

public class TopicsWithoutADisplayName
{
    public static readonly NotificationTopic Topic = SalesTopics.Staff("Sales.Invoice.Created", displayName: " ");
}

public class TopicsWithoutADescription
{
    public static readonly NotificationTopic Topic = new()
    {
        ID = "Sales.Invoice.Created",
        DisplayName = "A topic",
        Description = "",
        ReadPermission = NotificationTestActions.Invoices,
    };
}

public class StaffTopicsWithoutAReadPermission
{
    public static readonly NotificationTopic Topic = new()
    {
        ID = "Sales.Invoice.Created",
        DisplayName = "A topic",
        Description = "A description.",
        AllowEmails = true,
    };
}

public class CustomerTopicsWithAReadPermission
{
    public static readonly NotificationTopic Topic = new()
    {
        ID = "Sales.Invoice.Paid",
        DisplayName = "A topic",
        Description = "A description.",
        ForCustomers = true,
        ReadPermission = NotificationTestActions.Invoices,
    };
}

public class CustomerTopicsThatAllowEmails
{
    public static readonly NotificationTopic Topic = new()
    {
        ID = "Sales.Invoice.Paid",
        DisplayName = "A topic",
        Description = "A description.",
        ForCustomers = true,
        AllowEmails = true,
    };
}

/// <summary>
/// The topic contract, how a host registers its topics, the catalog, and the opt-in. The catalog is
/// read through the service provider, the way the rest of the feature reads it.
/// </summary>
public class NotificationTopicTests
{
    private static NotificationTopicCatalog BuildCatalog(string systemName, Action<NotificationOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddNotifications<NotificationTestDbContext>(systemName, configure);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<NotificationTopicCatalog>();
    }

    private static InvalidOperationException AssertFailsAtStartup<TTopics>() where TTopics : class
    {
        // The registration itself throws. No service provider is built and nothing is resolved.
        return Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddNotifications<NotificationTestDbContext>("Sales", o => o.AddTopics<TTopics>()));
    }

    // ── Registering and listing ─────────────────────────────────────────────────

    [Fact]
    public void AddTopics_FindsPublicStaticFields_IncludingNestedClasses_InOrder()
    {
        var catalog = BuildCatalog("Sales", o => o.AddTopics<SalesTopics>());

        Assert.Equal("Sales", catalog.SystemName);
        Assert.Equal(
            [SalesTopics.InvoiceCreated, SalesTopics.Customers.InvoicePaid],
            catalog.Topics);
    }

    [Fact]
    public void AddTopics_AcceptsAStaticClass_ThroughItsType()
    {
        // C# doesn't allow a static class as a generic argument, so hosts that use one need this overload.
        var catalog = BuildCatalog("Sales", o => o.AddTopics(typeof(SalesTopics.Customers)));

        Assert.Equal([SalesTopics.Customers.InvoicePaid], catalog.Topics);
    }

    [Fact]
    public void Topic_FlagsAreOffByDefault()
    {
        var topic = SalesTopics.Staff("Sales.Invoice.Created");

        Assert.False(topic.AllowEmails);
        Assert.False(topic.ForCustomers);
    }

    [Fact]
    public void AddNotifications_CalledTwiceWithTheSameSystem_AddsToTheSameCatalog()
    {
        var services = new ServiceCollection();
        services.AddNotifications<NotificationTestDbContext>("Sales", o => o.AddTopics(typeof(SalesTopics.Customers)));
        services.AddNotifications<NotificationTestDbContext>("Sales", o => o.AddTopics<SingleTopic>());

        using var provider = services.BuildServiceProvider();

        Assert.Equal(2, provider.GetRequiredService<NotificationTopicCatalog>().Topics.Count);
    }

    public class SingleTopic
    {
        public static readonly NotificationTopic Topic = SalesTopics.Staff("Sales.Invoice.Created");
    }

    // ── Looking up ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryGetTopic_KnownID_ReturnsTheTopic()
    {
        var catalog = BuildCatalog("Sales", o => o.AddTopics<SalesTopics>());

        Assert.True(catalog.TryGetTopic("Sales.Invoice.Created", out var topic));
        Assert.Same(SalesTopics.InvoiceCreated, topic);
    }

    [Theory]
    [InlineData("Sales.Invoice.Deleted")]    // never declared
    [InlineData("Sales.Invoice.Hidden")]     // a private field isn't a topic
    [InlineData("sales.invoice.created")]    // lookups are exact
    [InlineData("Invoice created")]          // the display name is not an ID
    [InlineData("")]
    public void TryGetTopic_UnknownID_ReportsItAsUnknown(string id)
    {
        var catalog = BuildCatalog("Sales", o => o.AddTopics<SalesTopics>());

        Assert.False(catalog.TryGetTopic(id, out var topic));
        Assert.Null(topic);
    }

    [Fact]
    public void ID_SurvivesADisplayNameChange()
    {
        // The same ID in two versions of the host's code, with the display name changed in between.
        // What a subscribers list stored under the first version still finds the topic in the second.
        const string storedInAList = "Sales.Invoice.Created";

        var before = BuildCatalog("Sales", o => o.AddTopics<RenamedTopicsBefore>());
        var after = BuildCatalog("Sales", o => o.AddTopics<RenamedTopicsAfter>());

        Assert.True(before.TryGetTopic(storedInAList, out var old));
        Assert.Equal("Invoice created", old.DisplayName);

        Assert.True(after.TryGetTopic(storedInAList, out var renamed));
        Assert.Equal("New invoice", renamed.DisplayName);
        Assert.Equal(old.ID, renamed.ID);
    }

    public class RenamedTopicsBefore
    {
        public static readonly NotificationTopic Topic = SalesTopics.Staff("Sales.Invoice.Created", displayName: "Invoice created");
    }

    public class RenamedTopicsAfter
    {
        public static readonly NotificationTopic Topic = SalesTopics.Staff("Sales.Invoice.Created", displayName: "New invoice");
    }

    // ── Failing at startup ──────────────────────────────────────────────────────

    [Fact]
    public void DuplicateID_FailsAtStartup()
    {
        var exception = AssertFailsAtStartup<DuplicateTopics>();

        Assert.Contains("Sales.Invoice.Created", exception.Message);
    }

    [Fact]
    public void DuplicateID_DifferingOnlyInCase_FailsAtStartup()
    {
        AssertFailsAtStartup<DuplicateTopicsInAnotherCase>();
    }

    [Fact]
    public void DuplicateID_AcrossTwoCalls_FailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddNotifications<NotificationTestDbContext>("Sales", o => o.AddTopics<SalesTopics>());

        Assert.Throws<InvalidOperationException>(() =>
            services.AddNotifications<NotificationTestDbContext>("Sales", o => o.AddTopics<SingleTopic>()));
    }

    [Fact]
    public void IDFromAnotherSystem_FailsAtStartup()
    {
        var exception = AssertFailsAtStartup<TopicsFromAnotherSystem>();

        Assert.Contains("Stock.Product.Created", exception.Message);
    }

    [Fact]
    public void IDThatOnlyLooksLikeTheSystem_FailsAtStartup()
    {
        AssertFailsAtStartup<TopicsWithALongerSystemName>();
        AssertFailsAtStartup<TopicsWithSystemNameInAnotherCase>();
    }

    [Fact]
    public void MalformedID_FailsAtStartup()
    {
        AssertFailsAtStartup<TopicsWithNothingAfterTheSystemName>();
        AssertFailsAtStartup<TopicsWithASpaceInTheID>();
    }

    [Fact]
    public void MissingDisplayNameOrDescription_FailsAtStartup()
    {
        AssertFailsAtStartup<TopicsWithoutADisplayName>();
        AssertFailsAtStartup<TopicsWithoutADescription>();
    }

    [Fact]
    public void StaffTopicWithoutAReadPermission_FailsAtStartup()
    {
        var exception = AssertFailsAtStartup<StaffTopicsWithoutAReadPermission>();

        Assert.Contains("read permission", exception.Message);
    }

    [Fact]
    public void CustomerTopicWithAReadPermissionOrEmails_FailsAtStartup()
    {
        AssertFailsAtStartup<CustomerTopicsWithAReadPermission>();
        AssertFailsAtStartup<CustomerTopicsThatAllowEmails>();
    }

    [Fact]
    public void ClassWithNoTopics_FailsAtStartup()
    {
        AssertFailsAtStartup<NoTopics>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Sales.")]
    [InlineData("Sales Dept")]
    public void BadSystemName_FailsAtStartup(string systemName)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddNotifications<NotificationTestDbContext>(systemName, o => o.AddTopics<SalesTopics>()));
    }

    [Fact]
    public void TwoSystemNamesInOneHost_FailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddNotifications<NotificationTestDbContext>("Sales", o => o.AddTopics<SalesTopics>());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNotifications<NotificationTestDbContext>("Stock", o => o.AddTopics<TopicsFromAnotherSystem>()));

        Assert.Contains("Sales", exception.Message);
        Assert.Contains("Stock", exception.Message);
    }

    // ── The opt-in ──────────────────────────────────────────────────────────────

    private static bool IsFromThisFeature(ServiceDescriptor descriptor)
    {
        // The framework's own namespaces. Scanning this test project also finds its test repository, which isn't the feature.
        static bool InFeature(Type? type) => type?.Namespace is "ShiftSoftware.ShiftEntity.Core.Notifications" or "ShiftSoftware.ShiftEntity.EFCore.Notifications";

        return InFeature(descriptor.ServiceType)
            || InFeature(descriptor.ImplementationInstance?.GetType() ?? descriptor.ImplementationType);
    }

    [Fact]
    public void HostThatDoesNotOptIn_RegistersNothing()
    {
        // The registrations a host always makes, and no AddNotifications.
        var services = new ServiceCollection();
        services.AddControllers().AddShiftEntityWeb(o => o.AddDataAssembly(typeof(SalesTopics).Assembly));
        services.RegisterShiftRepositories(typeof(SalesTopics).Assembly);

        Assert.NotEmpty(services);
        Assert.DoesNotContain(services, IsFromThisFeature);

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<NotificationTopicCatalog>());
    }

    [Fact]
    public void HostThatOptsIn_IsSeenByTheSameCheck()
    {
        // Proves the check in the test above can see this feature, so its pass means something.
        var services = new ServiceCollection();
        services.AddNotifications<NotificationTestDbContext>("Sales", o => o.AddTopics<SalesTopics>());

        Assert.Contains(services, IsFromThisFeature);
    }
}
