using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core.Notifications;
using Xunit;
using Topics = ShiftSoftware.ShiftEntity.Tests.Notifications.NotificationTestTopics;

namespace ShiftSoftware.ShiftEntity.Tests.Notifications;

/// <summary>The opt-in interface and the watcher types. The send path tests cover what happens to the watchers.</summary>
public class NotificationWatcherTests
{
    [Fact]
    public void AWatcher_CanBeAUser()
    {
        var watcher = NotificationWatcher.User(7, Topics.Staff, Topics.StaffUsersOnly);

        Assert.Equal(7, watcher.UserID);
        Assert.Null(watcher.CustomerEmail);
        Assert.True(watcher.Watches(Topics.Staff.ID));
        Assert.True(watcher.Watches(Topics.StaffUsersOnly.ID));
        Assert.False(watcher.Watches(Topics.Customers.ID));
    }

    [Fact]
    public void AWatcher_CanBeACustomer_AtATrimmedAddress()
    {
        var watcher = NotificationWatcher.Customer("  customer@example.com ", Topics.Customers);

        Assert.Null(watcher.UserID);
        Assert.Equal("customer@example.com", watcher.CustomerEmail);
        Assert.True(watcher.Watches(Topics.Customers.ID));
    }

    [Fact]
    public void EmptyValuesFromTheRecord_AreFine()
    {
        Assert.Null(NotificationWatcher.User(null, Topics.Staff).UserID);
        Assert.Null(NotificationWatcher.Customer(null, Topics.Customers).CustomerEmail);
        Assert.Null(NotificationWatcher.Customer("  ", Topics.Customers).CustomerEmail);
    }

    [Fact]
    public async Task AnEmptyResult_IsFine()
    {
        await using var app = await NotificationTestApp.StartAsync();

        Assert.Empty(await app.SaveRaiseAndDrainAsync(Topics.Staff, new NotifiedRecord { TestWatchers = [] }));
    }

    [Fact]
    public void AnEntityThatDoesNotOptIn_HasNoWatchers()
    {
        // The raise looks for this interface and nothing else.
        Assert.False(typeof(IHasNotificationWatchers).IsAssignableFrom(typeof(PlainRecord)));
    }

    [Fact]
    public void NothingIsAddedToTheEntitysTable()
    {
        var services = new ServiceCollection();
        services.AddDbContext<NotificationTestDbContext>(options => options.UseSqlite("Data Source=:memory:"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>().Model;

        // The same columns, with the interface and without it.
        static string[] Columns(Microsoft.EntityFrameworkCore.Metadata.IEntityType type)
            => type.GetProperties().Select(property => property.Name).Order().ToArray();

        var watched = model.FindEntityType(typeof(NotifiedRecord))!;
        var plain = model.FindEntityType(typeof(PlainRecord))!;

        Assert.Equal(Columns(plain), Columns(watched));
        Assert.Empty(watched.GetNavigations());
    }
}
