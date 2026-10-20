using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// An empty catalog that a test creates on the local SQL Server and drops afterwards, for what only SQL Server does:
/// system-versioned tables, ambient transactions, and its own form of the set-based bookkeeping statement. A test that
/// finds no local SQL Server is skipped. <c>SHIFT_ENTITY_TEST_SQL</c> names another local server, and
/// <c>SHIFT_ENTITY_TEST_SQL_PREFIX</c> starts the catalog's name (<c>ShiftEntityTests_</c> by default). It refuses any
/// server that is not local, and drops only a catalog that still holds the ownership marker it wrote.
/// </summary>
public sealed class OwnedSqlCatalog : IAsyncDisposable
{
    private static readonly Lazy<Task<string?>> unavailable = new(FindServerAsync);

    private readonly string serverConnectionString;
    private readonly Guid ownership = Guid.NewGuid();

    public string Name { get; }

    /// <summary>The connection string of the new catalog.</summary>
    public string ConnectionString { get; }

    private OwnedSqlCatalog(string name)
    {
        var builder = Server();
        Name = (Environment.GetEnvironmentVariable("SHIFT_ENTITY_TEST_SQL_PREFIX") ?? "ShiftEntityTests_") + name + "_" + Guid.NewGuid().ToString("N");

        builder.InitialCatalog = "master";
        serverConnectionString = builder.ConnectionString;
        builder.InitialCatalog = Name;
        ConnectionString = builder.ConnectionString;
    }

    private static SqlConnectionStringBuilder Server()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHIFT_ENTITY_TEST_SQL") ??
            @"Server=.\SQLEXPRESS;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=5");

        // An override supplies local connection details, never a remote application database.
        if (!Regex.IsMatch(builder.DataSource, @"^(?:\.\\SQLEXPRESS|localhost(?:\\\w+)?(?:,\d+)?|127\.0\.0\.1(?:,\d+)?|\(localdb\)\\\w+)$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("SQL Server tests need a local disposable SQL Server.");
        if (!string.IsNullOrEmpty(builder.InitialCatalog) && builder.InitialCatalog != "master")
            throw new InvalidOperationException("Do not give the SQL Server tests an existing database.");

        return builder;
    }

    // Why no local SQL Server can be used, or null when one can. Asked once per test run.
    private static async Task<string?> FindServerAsync()
    {
        try
        {
            var builder = Server();
            builder.InitialCatalog = "master";
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            return null;
        }
        catch (SqlException ex)
        {
            return $"No local SQL Server answered ({ex.Message.Split('\n')[0]}).";
        }
    }

    /// <summary>
    /// Creates the catalog and writes the ownership marker; skips the test when no local SQL Server answers. The name
    /// is the prefix, then <paramref name="name"/>, then a unique part.
    /// </summary>
    public static async Task<OwnedSqlCatalog> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (await unavailable.Value is { } reason)
            Assert.Skip(reason);

        var catalog = new OwnedSqlCatalog(name);

        await using (var server = new SqlConnection(catalog.serverConnectionString))
        {
            await server.OpenAsync(cancellationToken);
            await using var create = server.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{catalog.Name}]";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var owned = new SqlConnection(catalog.ConnectionString))
        {
            await owned.OpenAsync(cancellationToken);
            await using var mark = owned.CreateCommand();
            mark.CommandText = "CREATE TABLE [dbo].[TestOwnership] ([Marker] uniqueidentifier NOT NULL PRIMARY KEY); " +
                "INSERT INTO [dbo].[TestOwnership] VALUES (@marker)";
            mark.Parameters.AddWithValue("@marker", catalog.ownership);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        return catalog;
    }

    /// <summary>
    /// Sets options of the catalog itself from a connection to the server, for example
    /// <c>COMPATIBILITY_LEVEL = 120</c>. The catalog's pooled connections are cleared first.
    /// </summary>
    public async Task SetAsync(string options, CancellationToken cancellationToken = default)
    {
        SqlConnection.ClearPool(new SqlConnection(ConnectionString));
        await using var server = new SqlConnection(serverConnectionString);
        await server.OpenAsync(cancellationToken);
        await using var alter = server.CreateCommand();
        alter.CommandText = $"ALTER DATABASE [{Name}] SET {options}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var owned = new SqlConnection(ConnectionString))
        {
            await owned.OpenAsync();
            await using var check = owned.CreateCommand();
            check.CommandText = "SELECT [Marker] FROM [dbo].[TestOwnership]";
            if (await check.ExecuteScalarAsync() is not Guid marker || marker != ownership)
                throw new InvalidOperationException("Database ownership marker mismatch; refusing teardown.");
        }

        // Only this catalog's pooled connections must go before the drop.
        SqlConnection.ClearPool(new SqlConnection(ConnectionString));
        await using var server = new SqlConnection(serverConnectionString);
        await server.OpenAsync();
        await using var drop = server.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]";
        await drop.ExecuteNonQueryAsync();
    }
}
