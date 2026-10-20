using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace ShiftSoftware.ShiftEntity.EFCore.Replication;

/// <summary>
/// What a root entity's replicated document is made of: the rows reached through the includes the root declares with
/// <see cref="ReplicationTreeExtensions.HasReplicationTree{TRoot}"/>. The same declaration loads the root to build its
/// document, and tells a save which roots a changed row belongs to.
/// </summary>
public sealed class ReplicationTree
{
    internal ReplicationTree(Type rootType, LambdaExpression includes)
    {
        RootType = rootType;
        Includes = includes;
        Apply = (Func<IQueryable, IQueryable>)CompileApply(rootType, includes);
        Paths = ParsePaths(includes);
    }

    /// <summary>The root entity's CLR type.</summary>
    public Type RootType { get; }

    /// <summary>
    /// The navigation names of every row type in the tree, from the root: one list for each Include and ThenInclude,
    /// so <c>City</c>, <c>City.Region</c> and <c>City.Region.Country</c> are three paths.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Paths { get; }

    internal LambdaExpression Includes { get; }

    /// <summary>Applies the includes to a query of the root type.</summary>
    internal Func<IQueryable, IQueryable> Apply { get; }

    private static Delegate CompileApply(Type rootType, LambdaExpression includes)
    {
        // query => (IQueryable)includes((IQueryable<TRoot>)query)
        var query = Expression.Parameter(typeof(IQueryable), "query");
        var typed = Expression.Convert(query, typeof(IQueryable<>).MakeGenericType(rootType));
        var body = Expression.Convert(Expression.Invoke(includes, typed), typeof(IQueryable));
        return Expression.Lambda<Func<IQueryable, IQueryable>>(body, query).Compile();
    }

    // Reads q => q.Include(x => x.A).ThenInclude(a => a.B).Include(x => x.C) into [A], [A, B] and [C]. A collection
    // may be filtered or ordered inside its include (x => x.Items.OrderBy(i => i.Rank)); only its name counts here, and
    // the ordering is applied when the root is loaded.
    private static IReadOnlyList<IReadOnlyList<string>> ParsePaths(LambdaExpression includes)
    {
        var calls = new Stack<MethodCallExpression>();
        var expression = includes.Body;
        while (expression is MethodCallExpression call)
        {
            calls.Push(call);
            expression = call.Arguments[0];
        }

        if (expression != includes.Parameters[0])
            throw Invalid("it must start from the query it is given");

        var paths = new List<IReadOnlyList<string>>();
        List<string>? current = null;

        while (calls.TryPop(out var call))
        {
            if (call.Method.DeclaringType != typeof(EntityFrameworkQueryableExtensions) ||
                call.Method.Name is not (nameof(EntityFrameworkQueryableExtensions.Include) or nameof(EntityFrameworkQueryableExtensions.ThenInclude)) ||
                call.Arguments.Count != 2 || StripQuotes(call.Arguments[1]) is not LambdaExpression navigation)
                throw Invalid($"'{call.Method.Name}' is not an Include or ThenInclude with a navigation lambda");

            var name = NavigationName(navigation);

            if (call.Method.Name == nameof(EntityFrameworkQueryableExtensions.Include))
                current = [name];
            else if (current is null)
                throw Invalid("a ThenInclude must follow an Include");
            else
                current = [.. current, name];

            if (!paths.Any(x => x.SequenceEqual(current)))
                paths.Add(current);
        }

        return paths;
    }

    private static string NavigationName(LambdaExpression navigation)
    {
        var body = navigation.Body;

        // A filtered include: x => x.Items.Where(...).OrderBy(...). The navigation is the innermost member.
        while (body is MethodCallExpression call && call.Method.DeclaringType == typeof(Enumerable) &&
            call.Method.Name is "Where" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Skip" or "Take")
            body = call.Arguments[0];

        if (body is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            body = convert.Operand;

        if (body is MemberExpression member && member.Expression == navigation.Parameters[0])
            return member.Member.Name;

        throw Invalid($"'{navigation}' is not a navigation of the entity it starts from");
    }

    private static Expression StripQuotes(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;

    private static InvalidOperationException Invalid(string reason) =>
        new($"A replication tree is a chain of Include and ThenInclude calls on the query it is given, and {reason}.");
}

/// <summary>Declares replication trees.</summary>
public static class ReplicationTreeExtensions
{
    /// <summary>
    /// Declares what the replicated document of <typeparamref name="TRoot"/> is made of, written as the includes of a
    /// query: <c>q =&gt; q.Include(x =&gt; x.City).ThenInclude(x =&gt; x.Region).Include(x =&gt; x.Lines.OrderBy(l =&gt; l.Rank))</c>.
    /// A registration that replicates the root from its tree loads the root by ID with these includes after each save
    /// that changes a row in it, and a save of a row in the tree marks its roots as not replicated, in the same
    /// transaction. The declaration adds nothing to the EF model and changes no query of the application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declare it where the model is built: in <c>OnModelCreating</c>, or in an <see cref="IModelBuildingContributor"/>.
    /// Every row type in the tree must be an entity with a single <c>long</c> key named <c>ID</c>, and the root must
    /// implement <c>IShiftEntityReplication</c>.
    /// </para>
    /// <para>
    /// The shapes a save can follow: references at any depth, toward the row each foreign key points at (a branch's
    /// city, the city's region), and collections directly under the root whose rows point at the root through a foreign
    /// key property of their own (link rows), followed by references. Any other step, such as a collection under a
    /// collection or under a reference, or a reference to a dependent row, is refused when the model is built: a save
    /// could not find the roots that lose a row deleted or moved below it.
    /// </para>
    /// <para>
    /// The roots are marked whenever the context declares the tree: also under <c>SuppressReplication()</c>, in a
    /// process that replicates nothing, and in a save that does not accept its changes. The next dirty-only catch-up
    /// rebuilds them. A save that changes nothing but a row's <c>LastReplicationDate</c> and
    /// <c>LastReplicationStamp</c> marks nothing. Rows that a host's before-save step (a trigger or an interceptor)
    /// changes during the save are not followed to their roots.
    /// </para>
    /// <para>
    /// LOUD: the mark has no version. A sync or catch-up that loaded a root before a later change of its tree writes back
    /// the root's <c>LastSaveDate</c>, which a part change never moves, and so can clear the later change's mark. If the
    /// later sync then fails or lands first, the document keeps the older tree while the root looks replicated. Scripts
    /// and imports that edit tree parts must run a forced catch-up afterwards.
    /// </para>
    /// </remarks>
    public static EntityTypeBuilder<TRoot> HasReplicationTree<TRoot>(this EntityTypeBuilder<TRoot> builder,
        Expression<Func<IQueryable<TRoot>, IQueryable<TRoot>>> includes)
        where TRoot : class
    {
        ReplicationTrees.Declare(builder.Metadata.Model, new ReplicationTree(typeof(TRoot), includes));
        return builder;
    }
}

/// <summary>
/// The replication trees of each context type. A declaration is held against the model being built, and the context
/// claims it when EF finalizes that model, so nothing reaches the model's annotations or a migration snapshot.
/// </summary>
internal static class ReplicationTrees
{
    private static readonly ConditionalWeakTable<IReadOnlyModel, Dictionary<Type, ReplicationTree>> declared = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<Type, ReplicationTree>> claimed = new();

    public static void Declare(IReadOnlyModel model, ReplicationTree tree)
    {
        var trees = declared.GetOrCreateValue(model);
        lock (trees)
            trees[tree.RootType] = tree;
    }

    public static void Claim(Type contextType, IReadOnlyModel model)
    {
        Dictionary<Type, ReplicationTree> trees;
        if (declared.TryGetValue(model, out var found))
            lock (found)
                trees = new(found);
        else
            trees = new();

        claimed[contextType] = trees;
    }

    // A context's trees are claimed when EF builds its model, which a new context does only when something first
    // needs the model. Reading the model here builds it, so a host's own first query can ask for a tree.
    public static IReadOnlyDictionary<Type, ReplicationTree> For(DbContext context)
    {
        _ = context.Model;
        return claimed.TryGetValue(context.GetType(), out var trees) ? trees : EmptyTrees;
    }

    private static readonly IReadOnlyDictionary<Type, ReplicationTree> EmptyTrees = new Dictionary<Type, ReplicationTree>();

    /// <summary>Claims a context's trees when EF finalizes its model, and checks their shapes once it is final.</summary>
    internal sealed class Convention(Type contextType) : IModelFinalizingConvention, IModelFinalizedConvention
    {
        public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context) =>
            Claim(contextType, modelBuilder.Metadata);

        // A tree that a save could not follow fails the model here, when it is built, rather than a save later.
        public IModel ProcessModelFinalized(IModel model)
        {
            if (claimed.TryGetValue(contextType, out var trees) && trees.Count > 0)
                ReplicationTreeIndex.Validate(model, trees);
            return model;
        }
    }
}
