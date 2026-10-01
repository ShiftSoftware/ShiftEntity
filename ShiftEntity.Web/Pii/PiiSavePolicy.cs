using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web.Pii;

/// <summary>Resolves explicit save intent before a mapper can write protected entity strings.</summary>
internal sealed class PiiSavePolicy
{
    private readonly Dictionary<PropertyInfo, string?> values = new();

    public static PiiSavePolicy Prepare(object dto, object entity, IServiceProvider services, bool isCreate)
    {
        var policy = new PiiSavePolicy();
        var validationMessages = new List<Message>();
        var dtoType = dto.GetType();
        var entityType = entity.GetType();
        var typeAuth = services.GetRequiredService<ITypeAuthService>();
        var action = services.GetRequiredService<IOptions<PiiOptions>>().Value.Action;

        foreach (var member in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (member.PropertyType != typeof(PiiFieldDTO))
            {
                if (PiiDtoProtector.HasProtectedMembers(member.PropertyType))
                    throw Invalid("Nested protected fields cannot be changed by this save route.");
                continue;
            }
            if (PiiFieldProtection.FindDeclaration(member) is null)
                throw Invalid("A protected field is not classified.");
            var target = entityType.GetProperty(member.Name, BindingFlags.Public | BindingFlags.Instance);
            if (target?.PropertyType != typeof(string) || !target.CanRead || !target.CanWrite)
                throw Invalid("A protected field has no matching entity string.");

            var submitted = (PiiFieldDTO?)member.GetValue(dto);
            var intent = submitted?.Write ?? (submitted is null ? "keep" : null);
            if (intent is not ("keep" or "replace"))
                throw Invalid("Protected field write intent is missing or invalid.");

            var raw = intent == "keep" ? (string?)target.GetValue(entity) : submitted!.Value;
            if (intent == "replace" && !typeAuth.CanAccess(action))
                throw new ShiftEntityException(new Message("Forbidden", "Protected field change is not permitted."),
                    (int)HttpStatusCode.Forbidden);

            if (intent == "replace" || isCreate)
            {
                var results = new List<ValidationResult>();
                ValidateRawValue(member, dto, raw, results);
                ValidateRawValue(target, entity, raw, results);
                if (results.Count > 0)
                    validationMessages.Add(new Message
                    {
                        For = member.Name,
                        Title = member.Name,
                        SubMessages = results.Select(x => x.ErrorMessage ?? "The protected field value is invalid.")
                            .Distinct().Select(x => new Message { Title = x }).ToList()
                    });
            }

            // Mapping conventions read Value. Discard client Display and any Value submitted with keep.
            member.SetValue(dto, new PiiFieldDTO { Value = raw, Write = intent });
            policy.values.Add(target, raw);
        }

        if (validationMessages.Count > 0)
            throw new ShiftEntityException(new Message
            {
                Title = "Model Validation Error",
                SubMessages = validationMessages
            }, (int)HttpStatusCode.BadRequest);
        return policy;
    }

    public void ValidateMapped(object entity)
    {
        foreach (var (member, raw) in values)
            if (!string.Equals((string?)member.GetValue(entity), raw, StringComparison.Ordinal))
                throw new InvalidOperationException("The entity mapper did not preserve a protected field's resolved value.");
    }

    // Applies the validation attributes declared on a DTO or entity member to the resolved raw value.
    private static void ValidateRawValue(PropertyInfo member, object owner, string? raw, List<ValidationResult> results)
        => Validator.TryValidateValue(raw, new ValidationContext(owner) { MemberName = member.Name }, results,
            member.GetCustomAttributes<ValidationAttribute>(true));

    private static ShiftEntityException Invalid(string message)
        => new(new Message("Invalid protected field", message), (int)HttpStatusCode.BadRequest);
}
