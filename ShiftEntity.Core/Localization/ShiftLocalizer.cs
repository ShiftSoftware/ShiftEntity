using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.Localization;

public abstract class ShiftLocalizer : IStringLocalizer
{
    private readonly IStringLocalizer _loc;

    protected ShiftLocalizer(IServiceProvider services, Type resourceType)
    {
        var factory = services.GetRequiredService<IStringLocalizerFactory>();
        _loc = factory.Create(resourceType);
    }

    public LocalizedString this[string name] => _loc[name];

    public LocalizedString this[string name, params object[] arguments] => _loc[name, arguments];

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        return _loc.GetAllStrings(includeParentCultures);
    }
}
