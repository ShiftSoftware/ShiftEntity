using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Pii;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Web.Pii;

// Collection selectors carry child IDs, never list positions: Phones[child-id].Number.
internal sealed class PiiMemberPath
{
    private sealed record Step(PropertyInfo DtoMember, PropertyInfo EntityMember, string? ChildKey, Type? DtoElement);
    private readonly List<Step> steps = new();

    public static PiiMemberPath? Parse(Type dtoType, Type entityType, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048)
            return null;
        var tokens = path.Split('.');
        if (tokens.Length > 32)
            return null;
        var result = new PiiMemberPath();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var bracket = token.IndexOf('[');
            var name = bracket < 0 ? token : token.Substring(0, bracket);
            string? key = null;
            if (bracket >= 0)
            {
                if (!token.EndsWith("]", StringComparison.Ordinal) || token.LastIndexOf('[') != bracket)
                    return null;
                key = token.Substring(bracket + 1, token.Length - bracket - 2);
                if (string.IsNullOrWhiteSpace(key) || key.Contains(']')) return null;
            }
            var dto = dtoType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var entity = entityType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (dto is null || entity is null || !dto.CanRead || !entity.CanRead
                || dto.GetIndexParameters().Length != 0 || entity.GetIndexParameters().Length != 0)
                return null;
            if (index == tokens.Length - 1)
            {
                if (key is not null || dto.PropertyType != typeof(PiiFieldDTO) || entity.PropertyType != typeof(string)
                    || PiiFieldProtection.FindDeclaration(dto) is not { Revealable: true })
                    return null;
                result.steps.Add(new Step(dto, entity, null, null));
                return result;
            }
            var dtoElement = PiiGraphIdentity.ElementType(dto.PropertyType);
            var entityElement = PiiGraphIdentity.ElementType(entity.PropertyType);
            if (dtoElement is not null || entityElement is not null)
            {
                if (dtoElement is null || entityElement is null || key is null
                    || !PiiGraphIdentity.HasIds(dtoElement, entityElement))
                    return null;
                result.steps.Add(new Step(dto, entity, key, dtoElement));
                dtoType = dtoElement;
                entityType = entityElement;
            }
            else
            {
                if (key is not null || !PiiDtoProtector.HasProtectedMembers(dto.PropertyType))
                    return null;
                result.steps.Add(new Step(dto, entity, null, null));
                dtoType = dto.PropertyType;
                entityType = entity.PropertyType;
            }
        }
        return null;
    }

    public bool TryRead(object entity, IHashIdService hashes, out string? value)
    {
        object? current = entity;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (current is null) { value = null; return false; }
            current = step.EntityMember.GetValue(current);
            if (step.ChildKey is not null)
            {
                // The client sends the child ID in the same encoded form as the DTO's ID.
                long id;
                try { id = hashes.Decode(step.ChildKey, step.DtoElement!); }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                { value = null; return false; }

                var matches = PiiGraphIdentity.Items(current).Where(x => PiiGraphIdentity.EntityId(x) == id).ToArray();
                if (id <= 0 || matches.Length != 1) { value = null; return false; }
                current = matches[0];
            }
        }
        value = (string?)current;
        return true;
    }
}
