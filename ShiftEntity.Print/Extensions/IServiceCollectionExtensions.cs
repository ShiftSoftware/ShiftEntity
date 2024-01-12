using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Print;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddShiftEntityPrint(this IServiceCollection services, ShiftEntityPrintOptions options)
    {
        services.AddScoped<ShiftEntityPrintOptions>(x => options);

        return services;
    }

    public static IServiceCollection AddShiftEntityPrint(this IServiceCollection services, Action<ShiftEntityPrintOptions> optionsBuilder)
    {
        ShiftEntityPrintOptions o = new();
        optionsBuilder.Invoke(o);

        return AddShiftEntityPrint(services, o);
    }
}
