using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShiftSoftware.ShiftEntity.EFCore.Migrations;

namespace ShiftSoftware.ShiftEntity.EFCore;

internal class ShiftDbContextExtensionOptions : IDbContextOptionsExtension
{
    public bool UseTemporal { get; set; }

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        if (UseTemporal)
        {
            services.Replace(ServiceDescriptor.Scoped<IMigrationsModelDiffer, ShiftEntityMigrationsModelDiffer>());
        }
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(IDbContextOptionsExtension extension) : base(extension)
        {
        }

        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "ShiftDbContextExtensionOptions";
        public override int GetServiceProviderHashCode() => 0;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
        }

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
        {
            return true;
        }
    }
}
