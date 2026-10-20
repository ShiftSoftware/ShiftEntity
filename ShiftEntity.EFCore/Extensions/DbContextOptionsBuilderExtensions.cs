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

    /// <summary>
    /// Lets a <see cref="ShiftDbContext"/> hold the work its saves ask to run after the commit
    /// (<see cref="ShiftDbContext.AfterCommit"/>) until an explicit transaction commits, and drop it when the
    /// transaction rolls back. Without this, such work runs right after the save even inside an explicit transaction.
    /// Calling it more than once is harmless.
    /// </summary>
    public static DbContextOptionsBuilder UseShiftEntityAfterCommit(this DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(ShiftDbContextTransactionInterceptor.Instance);
}
