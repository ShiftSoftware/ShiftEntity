using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Core.Phones;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.TypeAuth.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web.Pii;

/// <summary>Resolves explicit save intent before a mapper can write protected entity strings.</summary>
internal sealed class PiiSavePolicy
{
    private readonly Dictionary<PropertyInfo, string?> values = new();
    private readonly List<Child> children = new();
    private sealed record Child(PropertyInfo Member, int? Index, long? Id, PiiSavePolicy Policy);

    public static PiiSavePolicy Prepare(object dto, object entity, IServiceProvider services, bool isCreate)
    {
        var errors = new List<Message>();
        var action = services.GetRequiredService<IOptions<PiiOptions>>().Value.Action;
        var allowed = services.GetRequiredService<ITypeAuthService>().CanAccess(action);
        var policy = PrepareNode(dto, entity, services, allowed, isCreate, "", errors,
            new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        if (errors.Count > 0)
            throw new ShiftEntityException(new Message
            {
                Title = "Model Validation Error", SubMessages = errors
            }, (int)HttpStatusCode.BadRequest);
        return policy;
    }

    private static PiiSavePolicy PrepareNode(object dto, object entity, IServiceProvider services, bool allowed, bool isCreate,
        string path, List<Message> errors, HashSet<object> visiting, int depth)
    {
        if (depth > 32 || !visiting.Add(dto))
            throw Invalid("Protected save graphs cannot contain cycles or exceed the supported depth.");
        try
        {
            var policy = new PiiSavePolicy();
            foreach (var member in dto.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!member.CanRead || member.GetIndexParameters().Length != 0)
                    continue;
                var fieldPath = path + member.Name;
                var target = entity.GetType().GetProperty(member.Name, BindingFlags.Public | BindingFlags.Instance);
                if (member.PropertyType == typeof(PiiFieldDTO))
                {
                    if (!member.CanWrite || PiiFieldProtection.FindDeclaration(member) is null)
                        throw Invalid("A protected field needs a writable, classified DTO member.");
                    if (target?.PropertyType != typeof(string) || !target.CanRead || !target.CanWrite)
                        throw Invalid("A protected field has no matching entity string.");
                    var submitted = (PiiFieldDTO?)member.GetValue(dto);
                    var intent = submitted?.Write ?? (submitted is null ? "keep" : null);
                    if (intent is not ("keep" or "replace"))
                        throw Invalid("Protected field write intent is missing or invalid.");
                    if (intent == "replace" && !allowed)
                        throw new ShiftEntityException(new Message("Forbidden", "Protected field change is not permitted."),
                            (int)HttpStatusCode.Forbidden);
                    var raw = intent == "keep" ? (string?)target.GetValue(entity) : submitted!.Value;
                    if (intent == "replace" || isCreate)
                    {
                        var results = new List<ValidationResult>();
                        ValidateRawValue(member, dto, raw, results, services);
                        if (results.Count == 0 && raw is not null && PiiFieldProtection.FindDeclaration(member)?.Kind == PiiKind.Phone
                            && services.GetService<IPhoneNumberService>() is { } phones)
                        {
                            if (phones.TryNormalize(raw, out var normalized, out var error))
                                raw = normalized;
                            else
                                results.Add(new ValidationResult(error));
                        }
                        ValidateRawValue(target, entity, raw, results, services);
                        if (results.Count > 0)
                            errors.Add(new Message
                            {
                                For = fieldPath, Title = fieldPath,
                                SubMessages = results.Select(x => x.ErrorMessage ?? "The protected field value is invalid.")
                                    .Distinct().Select(x => new Message { Title = x }).ToList()
                            });
                    }
                    member.SetValue(dto, new PiiFieldDTO { Value = raw, Write = intent });
                    policy.values.Add(target, raw);
                    continue;
                }
                if (!PiiDtoProtector.HasProtectedMembers(member.PropertyType))
                    continue;
                var submittedChild = member.GetValue(dto);
                // Null objects and omitted collection items follow the host's ordinary removal rules.
                if (submittedChild is null)
                    continue;
                if (target is null || !target.CanRead)
                    throw Invalid("A protected child has no matching entity member.");
                var storedChild = target.GetValue(entity);
                if (PiiGraphIdentity.ElementType(member.PropertyType) is { } dtoElement)
                {
                    var entityElement = PiiGraphIdentity.ElementType(target.PropertyType)
                        ?? throw Invalid("A protected collection has no matching entity collection.");
                    if (!PiiGraphIdentity.HasIds(dtoElement, entityElement))
                        throw Invalid("Protected collection items need framework IDs on the DTO and the entity.");
                    var stored = new Dictionary<long, object>();
                    foreach (var item in PiiGraphIdentity.Items(storedChild))
                    {
                        if (PiiGraphIdentity.EntityId(item) is not { } storedId || !stored.TryAdd(storedId, item))
                            throw Invalid("Stored protected collection items need unique IDs.");
                    }
                    var submitted = PiiGraphIdentity.Items(submittedChild);
                    var seen = new HashSet<long>();
                    for (var i = 0; i < submitted.Length; i++)
                    {
                        var item = submitted[i] ?? throw Invalid("Protected collection items cannot be null.");
                        var id = PiiGraphIdentity.DtoId(item);
                        if (id is { } submittedId && !seen.Add(submittedId))
                            throw Invalid("Protected collection item IDs must be unique.");
                        object? original = null;
                        if (id is { } childId && !stored.TryGetValue(childId, out original) && !isCreate)
                            throw Invalid("A protected child ID does not belong to the loaded record.");
                        var fresh = original ?? NewEntity(entityElement);
                        policy.children.Add(new Child(target, i, id, PrepareNode(item, fresh, services, allowed,
                            isCreate || original is null, fieldPath + "[" + i + "].", errors, visiting, depth + 1)));
                    }
                }
                else
                {
                    var original = storedChild;
                    var id = PiiGraphIdentity.DtoId(submittedChild);
                    if (!isCreate && id is not null && id != PiiGraphIdentity.EntityId(original))
                        throw Invalid("A protected child ID does not belong to the loaded record.");
                    policy.children.Add(new Child(target, null, id, PrepareNode(submittedChild,
                        original ?? NewEntity(target.PropertyType), services, allowed, isCreate || original is null,
                        fieldPath + ".", errors, visiting, depth + 1)));
                }
            }
            return policy;
        }
        finally { visiting.Remove(dto); }
    }

    public void ValidateMapped(object entity)
    {
        foreach (var (member, raw) in values)
            if (!string.Equals((string?)member.GetValue(entity), raw, StringComparison.Ordinal))
                throw new InvalidOperationException("The entity mapper did not preserve a protected field's resolved value.");
        foreach (var child in children)
        {
            var value = child.Member.GetValue(entity);
            if (child.Index is { } index)
            {
                var items = PiiGraphIdentity.Items(value);
                var matches = child.Id is null ? Array.Empty<object>()
                    : items.Where(x => PiiGraphIdentity.EntityId(x) == child.Id).ToArray();
                // Generated collection maps may recreate child rows with new database IDs.
                // Only an item without an assigned ID may fall back to its submitted position.
                value = matches.Length == 1 ? matches[0]
                    : matches.Length == 0 && index < items.Length && PiiGraphIdentity.EntityId(items[index]) is null
                        ? items[index] : null;
            }
            if (value is null || child.Index is null && child.Id is not null
                && PiiGraphIdentity.EntityId(value) is { } mappedId && mappedId != child.Id)
                throw new InvalidOperationException("The entity mapper did not preserve a protected child.");
            child.Policy.ValidateMapped(value);
        }
    }

    // Applies the validation attributes declared on a DTO or entity member to the resolved raw value.
    private static void ValidateRawValue(PropertyInfo member, object owner, string? raw, List<ValidationResult> results, IServiceProvider services)
        => Validator.TryValidateValue(raw, new ValidationContext(owner, services, null) { MemberName = member.Name }, results,
            member.GetCustomAttributes<ValidationAttribute>(true));

    private static object NewEntity(Type type)
    {
        if (type.IsValueType || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
            throw Invalid("New protected children need a constructible entity type.");
        return Activator.CreateInstance(type)!;
    }

    private static ShiftEntityException Invalid(string message)
        => new(new Message("Invalid protected field", message), (int)HttpStatusCode.BadRequest);
}
