using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftSoftware.ShiftEntity.EFCore.Sqlite;

/// <summary>
/// What <see cref="ShiftEntitySqliteOptionsExtensions.UseCaseInsensitiveText"/> turns on: every text column without a
/// collation of its own gets SQLite's <c>NOCASE</c>, so it compares, sorts and is unique without case, as text does on
/// SQL Server under its default collation.
/// </summary>
/// <remarks>
/// <para>
/// SQLite compares text with case by default (<c>BINARY</c>), so without this a username saved as <c>Dave</c> does not
/// match <c>dave</c>. That default is left alone unless the consumer asks for this.
/// </para>
/// <para>
/// Every entity of the context, the consumer's and any library's. A property that already has a collation keeps it
/// (give one <c>UseCollation("BINARY")</c> to keep it compared with case), as does a property with a value converter
/// and a property stored inside a JSON column. <c>NOCASE</c> folds ASCII letters only, which is SQLite's own limit.
/// </para>
/// </remarks>
internal sealed class SqliteCaseInsensitiveText : IDbContextOptionsExtension
{
    internal const string Collation = "NOCASE";

    private DbContextOptionsExtensionInfo? info;

    public DbContextOptionsExtensionInfo Info => info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        new EntityFrameworkServicesBuilder(services).TryAdd<IConventionSetPlugin, ConventionSetPlugin>();

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ConventionSetPlugin : IConventionSetPlugin
    {
        public ConventionSet ModifyConventions(ConventionSet conventionSet)
        {
            conventionSet.ModelFinalizingConventions.Add(new Convention());
            return conventionSet;
        }
    }

    private sealed class Convention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
        {
            foreach (var entity in modelBuilder.Metadata.GetEntityTypes())
            {
                if (entity.IsMappedToJson())
                    continue;

                foreach (var property in entity.GetDeclaredProperties())
                    if (property.ClrType == typeof(string) && property.GetCollation() is null && property.GetValueConverter() is null)
                        property.Builder.UseCollation(Collation);
            }
        }
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "UseCaseInsensitiveText ";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["ShiftEntity:SqliteCaseInsensitiveText"] = "1";
    }
}
