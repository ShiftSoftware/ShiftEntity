using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core.DataLevelAccess;

/// <summary>
/// Turns a <see cref="DataLevelMatch{TEntity}"/> factory into one compiled predicate that takes the accessible ids as a
/// parameter. The row path then compiles the factory's expression once, instead of once per row check. The query path
/// is not affected: Entity Framework needs the expression with the ids in it, so it still calls the factory per query.
/// <para>
/// How it works. The factory is called twice, each time with a different placeholder collection. In each expression,
/// every place that reads the placeholder becomes a parameter. If the factory computed anything else from the
/// collection (a count, a copy, a set), that value differs between the two calls, so the two expressions differ, and
/// the factory cannot be reused for other ids. Then <see cref="TryCompile{TEntity}"/> returns null, and the caller
/// builds and compiles the expression per call, as before. The same happens when the factory throws for a
/// placeholder, or when its expression does not read the ids at all.
/// </para>
/// </summary>
internal static class MatchCompiler
{
    public static Func<TEntity, IReadOnlyCollection<long?>, bool>? TryCompile<TEntity>(DataLevelMatch<TEntity> match)
    {
        // Different contents on purpose: the count, the emptiness and the null entry all differ between the two.
        var first = new Placeholder(Array.Empty<long?>());
        var second = new Placeholder(new long?[] { null, 1, 2 });

        Expression<Func<TEntity, bool>> firstExpression;
        Expression<Func<TEntity, bool>> secondExpression;

        try
        {
            firstExpression = match(first);
            secondExpression = match(second);
        }
        catch
        {
            return null;
        }

        if (firstExpression is null || secondExpression is null)
            return null;

        var ids = Expression.Parameter(typeof(IReadOnlyCollection<long?>), "accessibleIds");

        // Compared with every captured value read out, so a value the factory computed per call shows up as a
        // difference.
        var firstReader = new Rewriter(first, ids, readCapturedValues: true);
        var secondReader = new Rewriter(second, ids, readCapturedValues: true);
        var firstShape = Shape.Of(firstReader.Visit(firstExpression));
        var secondShape = Shape.Of(secondReader.Visit(secondExpression));

        if (firstReader.Failed || secondReader.Failed || !firstShape.SequenceEqual(secondShape))
            return null;

        // Compiled with the captured values left in place, so a captured variable is read when the predicate runs,
        // exactly as in the factory's own expression.
        var compiler = new Rewriter(first, ids, readCapturedValues: false);
        var body = compiler.Visit(firstExpression.Body);

        if (compiler.Failed || !compiler.Replaced)
            return null;

        return Expression.Lambda<Func<TEntity, IReadOnlyCollection<long?>, bool>>(body, firstExpression.Parameters[0], ids).Compile();
    }

    /// <summary>A collection the factory cannot tell from real ids, except by its identity.</summary>
    private sealed class Placeholder(IReadOnlyList<long?> values) : IReadOnlyCollection<long?>
    {
        public int Count => values.Count;
        public IEnumerator<long?> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Replaces every read of the placeholder with the ids parameter. A read is a constant holding the placeholder, or
    /// a field of a captured object that holds it (a closure variable). With <c>readCapturedValues</c>, every other
    /// field read on a captured object becomes a constant of its current value, for the comparison.
    /// </summary>
    private sealed class Rewriter(Placeholder placeholder, ParameterExpression ids, bool readCapturedValues) : ExpressionVisitor
    {
        /// <summary>At least one read of the placeholder was replaced.</summary>
        public bool Replaced { get; private set; }

        /// <summary>A read of the placeholder expects a type that real ids may not have.</summary>
        public bool Failed { get; private set; }

        protected override Expression VisitConstant(ConstantExpression node)
            => ReferenceEquals(node.Value, placeholder) ? Ids(node.Type) : node;

        protected override Expression VisitMember(MemberExpression node)
        {
            var inner = Visit(node.Expression);

            // Only fields: a captured variable is a field. A property can compute anything, so it is never read here.
            if (inner is ConstantExpression { Value: { } owner } && node.Member is FieldInfo field)
            {
                var value = field.GetValue(owner);

                if (ReferenceEquals(value, placeholder))
                    return Ids(node.Type);

                if (readCapturedValues)
                    return Expression.Constant(value, node.Type);
            }

            return node.Update(inner);
        }

        private Expression Ids(Type type)
        {
            Replaced = true;

            if (type == ids.Type)
                return ids;

            if (type.IsAssignableFrom(ids.Type))
                return Expression.Convert(ids, type);

            Failed = true;
            return Expression.Default(type);
        }
    }

    /// <summary>
    /// A tree as a flat list of what each node is: its kind, its type, its member or method, and for a constant its
    /// value. Two calls of one factory that build the same tree give equal lists.
    /// </summary>
    private sealed class Shape : ExpressionVisitor
    {
        private readonly List<object?> parts = new();

        public static List<object?> Of(Expression expression)
        {
            var shape = new Shape();
            shape.Visit(expression);
            return shape.parts;
        }

        public override Expression? Visit(Expression? node)
        {
            if (node is null)
            {
                parts.Add(null);
                return node;
            }

            parts.Add(node.NodeType);
            parts.Add(node.Type);

            switch (node)
            {
                case ConstantExpression constant:
                    parts.Add(new ConstantValue(constant.Value));
                    break;
                case MemberExpression member:
                    parts.Add(member.Member);
                    break;
                case MethodCallExpression call:
                    parts.Add(call.Method);
                    break;
                case ParameterExpression parameter:
                    parts.Add(parameter.Name);
                    break;
                case BinaryExpression binary:
                    parts.Add(binary.Method);
                    break;
                case UnaryExpression unary:
                    parts.Add(unary.Method);
                    break;
                case NewExpression create:
                    parts.Add(create.Constructor);
                    break;
                case TypeBinaryExpression typeBinary:
                    parts.Add(typeBinary.TypeOperand);
                    break;
            }

            return base.Visit(node);
        }
    }

    /// <summary>A constant compared with <see cref="object.Equals(object, object)"/>, so two equal values match.</summary>
    private sealed class ConstantValue(object? value)
    {
        private readonly object? value = value;

        public override bool Equals(object? obj) => obj is ConstantValue other && Equals(value, other.value);
        public override int GetHashCode() => value?.GetHashCode() ?? 0;
    }
}
