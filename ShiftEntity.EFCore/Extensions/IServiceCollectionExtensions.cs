
using EntityFrameworkCore.Triggered.Transactions;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.EFCore.Triggers;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection RegisterShiftEntityEfCoreTriggers(this IServiceCollection services)
    {
        services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(GeneralTrigger<>));
        //services.AddTransient(typeof(IBeforeSaveTrigger<>), typeof(SetUserAndCompanyInfoTrigger<>)); ToDo: Create an Interface for getting user/company info
        services.AddTransient(typeof(IAfterSaveTrigger<>), typeof(ReloadAfterSaveTrigger<>));
        services.AddTransient(typeof(IBeforeCommitTrigger<>), typeof(BeforeCommitTrigger<>));

        return services;
    }
}
