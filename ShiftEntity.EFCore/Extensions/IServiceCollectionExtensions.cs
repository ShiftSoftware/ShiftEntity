
using EntityFrameworkCore.Triggered.Transactions;
using EntityFrameworkCore.Triggered;
using ShiftSoftware.ShiftEntity.EFCore.Triggers;
using ShiftSoftware.ShiftEntity.EFCore;
using System.Reflection;

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

    public static IServiceCollection RegisterShiftRepositories(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var repositoryTypes = assembly.GetTypes()
           .Where(type => typeof(ShiftRepositoryBase).IsAssignableFrom(type))
           .ToList();

            foreach (var type in repositoryTypes)
            {
                services.AddScoped(type);
            }
        }

        return services;
    }
}
