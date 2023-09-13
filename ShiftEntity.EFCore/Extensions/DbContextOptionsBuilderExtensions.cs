using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ShiftSoftware.ShiftEntity.EFCore.Extensions;

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
