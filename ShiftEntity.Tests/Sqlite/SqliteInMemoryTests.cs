using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Tests.DataProject;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Sqlite;

/// <summary>
/// A <c>ShiftDbContext</c> on in-memory SQLite: a shared in-memory database lives as long as the process even when no
/// context has a connection open, and an in-memory database that is not shared is logged once as a warning, never
/// refused. Each test uses a database name of its own, because the keep-alive lasts for the process.
/// </summary>
public class SqliteInMemoryTests
{
    private static string SharedName() => $"Data Source=shift-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static ServiceProvider Host(string connectionString, ConcurrentQueue<string>? warnings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (warnings is not null)
                b.AddProvider(new WarningLog(warnings));
        });
        services.AddDbContext<DataProjectDb>(o => o.UseSqlite(connectionString));
        return services.BuildServiceProvider();
    }

    private static async Task<int> CountAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DataProjectDb>().Credentials.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_shared_in_memory_database_keeps_its_rows_after_every_context_and_pooled_connection_closed()
    {
        var connectionString = SharedName();
        await using var provider = Host(connectionString);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataProjectDb>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Credentials.Add(new Credential { Name = "kept" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Closes every pooled connection, so only the keep-alive is left holding the database.
        SqliteConnection.ClearAllPools();

        Assert.Equal(1, await CountAsync(provider));
    }

    [Fact]
    public async Task Without_the_keep_alive_the_same_database_is_gone_once_its_last_connection_closes()
    {
        // The control for the test above: the same steps on connections of the test's own, outside any context.
        var connectionString = SharedName();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE T (X INTEGER)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        SqliteConnection.ClearAllPools();

        await using var again = new SqliteConnection(connectionString);
        await again.OpenAsync(TestContext.Current.CancellationToken);
        await using var check = again.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'T'";
        Assert.Equal(0L, await check.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("DataSource=:memory:")]
    [InlineData("Data Source=not-shared;Mode=Memory")]
    public async Task An_in_memory_database_that_is_not_shared_is_warned_about_once_and_still_works(string connectionString)
    {
        var warnings = new ConcurrentQueue<string>();
        await using var provider = Host(connectionString, warnings);

        // A context used for its model, and one that does all its work on one connection: both work.
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataProjectDb>();
            Assert.NotNull(db.Model.FindEntityType(typeof(Credential)));
            await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Credentials.Add(new Credential { Name = "one connection" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await db.Credentials.CountAsync(TestContext.Current.CancellationToken));
        }

        await using (var scope = provider.CreateAsyncScope())
            _ = scope.ServiceProvider.GetRequiredService<DataProjectDb>().Model;

        // Logged once per connection string for the process. Another test may already have logged the first one.
        Assert.True(warnings.Count(w => w.Contains("not shared") && w.Contains(connectionString)) <= 1);
    }

    [Fact]
    public async Task A_new_in_memory_connection_string_that_is_not_shared_logs_the_warning_with_the_form_to_use()
    {
        var connectionString = $"Data Source=not-shared-{Guid.NewGuid():N};Mode=Memory";
        var warnings = new ConcurrentQueue<string>();
        await using var provider = Host(connectionString, warnings);

        await using (var scope = provider.CreateAsyncScope())
            _ = scope.ServiceProvider.GetRequiredService<DataProjectDb>().Model;
        await using (var scope = provider.CreateAsyncScope())
            _ = scope.ServiceProvider.GetRequiredService<DataProjectDb>().Model;

        var warning = Assert.Single(warnings);
        Assert.Contains(connectionString, warning);
        Assert.Contains("Mode=Memory;Cache=Shared", warning);
    }

    [Theory]
    [InlineData("Data Source=shift-sqlite-test.db")]
    [InlineData("Data Source=shared-{0};Mode=Memory;Cache=Shared")]
    public async Task A_file_or_a_shared_in_memory_database_logs_nothing(string format)
    {
        var connectionString = string.Format(format, Guid.NewGuid().ToString("N"));
        var warnings = new ConcurrentQueue<string>();
        await using var provider = Host(connectionString, warnings);

        await using (var scope = provider.CreateAsyncScope())
            _ = scope.ServiceProvider.GetRequiredService<DataProjectDb>().Model;

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task A_context_given_an_open_connection_is_left_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var warnings = new ConcurrentQueue<string>();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new WarningLog(warnings)));
        services.AddDbContext<DataProjectDb>(o => o.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DataProjectDb>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Credentials.Add(new Credential { Name = "own connection" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await CountAsync(provider));
        Assert.Empty(warnings);
    }

    private sealed class WarningLog(ConcurrentQueue<string> warnings) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel >= LogLevel.Warning && message.Contains("SQLite connection string"))
                warnings.Enqueue(message);
        }

        public void Dispose() { }
    }
}
