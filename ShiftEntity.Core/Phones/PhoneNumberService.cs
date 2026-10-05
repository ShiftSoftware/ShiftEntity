using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PhoneNumbers;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ShiftSoftware.ShiftEntity.Core.Phones;

public interface IPhoneNumberService
{
    bool TryNormalize(string input, out string normalized, out string error);
}

/// <summary>Validates complete numbers using libphonenumber and formats them for the host's storage.</summary>
public sealed class PhoneNumberService(IOptions<PhoneNumberOptions> options) : IPhoneNumberService
{
    // The library returns a new region set. Keep one read-only copy for repeated validation.
    private static readonly Lazy<ISet<string>> supportedRegions = new(() => PhoneNumberUtil.GetInstance().GetSupportedRegions());

    public bool TryNormalize(string input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = "Enter a valid complete phone number.";
        if (string.IsNullOrWhiteSpace(input) || input.Length > 250)
            return false;

        var util = PhoneNumberUtil.GetInstance();
        var region = options.Value.DefaultRegion?.ToUpperInvariant();
        if (region is not null && !supportedRegions.Value.Contains(region))
        {
            error = "The default phone region is invalid. Configure an ISO phone region.";
            return false;
        }
        if (region is null && !input.TrimStart().StartsWith("+", StringComparison.Ordinal))
        {
            error = "Enter a phone number with + and its country code, or configure a default phone region.";
            return false;
        }
        try
        {
            // Country and national-prefix rules belong to the library's metadata.
            var number = util.Parse(input, region);
            // Extensions are separate dialing instructions and cannot be stored as the same phone key.
            if (!util.IsValidNumber(number) || number.HasExtension)
                return false;
            normalized = util.Format(number, options.Value.StorageFormat switch
            {
                PhoneStorageFormat.International => PhoneNumberFormat.INTERNATIONAL,
                PhoneStorageFormat.E164 => PhoneNumberFormat.E164,
                _ => throw new InvalidOperationException("Unsupported phone storage format.")
            });
            return true;
        }
        catch (NumberParseException) { return false; }
    }
}

/// <summary>Checks the raw value without mutating the member. Required handles missing values separately.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ValidPhoneNumberAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null || value is string { Length: 0 })
            return ValidationResult.Success;
        var service = validationContext.GetService(typeof(IPhoneNumberService)) as IPhoneNumberService;
        var error = "Configure phone number validation for this application.";
        if (service is not null && value is string input && service.TryNormalize(input, out _, out error))
            return ValidationResult.Success;
        return new ValidationResult(ErrorMessage ?? error,
            validationContext.MemberName is { } name ? new[] { name } : Array.Empty<string>());
    }
}

public static class PhoneNumberServiceCollectionExtensions
{
    public static IServiceCollection AddShiftPhoneNumbers(this IServiceCollection services, Action<PhoneNumberOptions>? configure = null)
    {
        services.AddOptions<PhoneNumberOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton<IPhoneNumberService, PhoneNumberService>();
        return services;
    }
}
