using Microsoft.EntityFrameworkCore.Infrastructure;
using ShiftSoftware.ShiftEntity.EFCore;

namespace Microsoft.EntityFrameworkCore;

public static class DbContextOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UseTemporal(this DbContextOptionsBuilder optionsBuilder, bool useTemporal = true)
    {
        var extension = optionsBuilder.Options.FindExtension<ShiftDbContextExtensionOptions>()
            ?? new ShiftDbContextExtensionOptions();

        extension.UseTemporal = useTemporal;

        var t = (IDbContextOptionsBuilderInfrastructure)optionsBuilder;
        t.AddOrUpdateExtension(extension);

        return optionsBuilder;
    }
}
