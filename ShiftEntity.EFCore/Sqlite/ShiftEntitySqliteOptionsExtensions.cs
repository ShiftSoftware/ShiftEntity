using Microsoft.EntityFrameworkCore.Infrastructure;
using ShiftSoftware.ShiftEntity.EFCore.Sqlite;

namespace Microsoft.EntityFrameworkCore;

/// <summary>ShiftEntity's options for a context on SQLite, set inside <c>UseSqlite</c>.</summary>
public static class ShiftEntitySqliteOptionsExtensions
{
    /// <summary>
    /// Makes every text column without a collation of its own compare without case (<c>NOCASE</c>), as text does on
    /// SQL Server under its default collation: a username saved as <c>Dave</c> then signs in as <c>dave</c>, and a
    /// unique index refuses both. SQLite compares with case by default, and that default stays unless this is called:
    /// <code>
    /// options.UseSqlite(connectionString, sqlite => sqlite.UseCaseInsensitiveText());
    /// </code>
    /// Every entity of the context is covered. A property with a collation of its own, a value converter or a place
    /// inside a JSON column keeps its own comparison; give a property <c>UseCollation("BINARY")</c> to keep it
    /// compared with case. Columns are created with the collation, so a database made before this was turned on keeps
    /// comparing with case until it is created again. Calling it more than once is harmless.
    /// </summary>
    public static SqliteDbContextOptionsBuilder UseCaseInsensitiveText(this SqliteDbContextOptionsBuilder sqliteOptionsBuilder)
    {
        var optionsBuilder = ((IRelationalDbContextOptionsBuilderInfrastructure)sqliteOptionsBuilder).OptionsBuilder;
        if (optionsBuilder.Options.FindExtension<SqliteCaseInsensitiveText>() is null)
            ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(new SqliteCaseInsensitiveText());

        return sqliteOptionsBuilder;
    }
}
