namespace ShiftSoftware.ShiftEntity.EFCore;

/// <summary>
/// The save handlers that a context or a module declares for its model: the handlers that keep its data right, which
/// must run in every host that saves its entity types. A context fills it in <c>ConfigureSaveHandlers</c>, and a
/// module in <see cref="IModelBuildingContributor.ConfigureSaveHandlers"/>.
/// </summary>
/// <remarks>
/// <para>
/// A handler class implements one interface for each stage it takes part in: <see cref="IBeforeSaveHandler{TEntity}"/>,
/// <see cref="IAfterSaveHandler{TEntity}"/>, <see cref="IBeforeCommitHandler{TEntity}"/>,
/// <see cref="IAfterCommitHandler{TEntity}"/>. It is declared for every one of them. A class that implements none
/// throws here. The same class declared twice is one handler: the first declaration counts.
/// </para>
/// <para>
/// An open generic class with one type parameter, such as <c>typeof(ChangeLog&lt;&gt;)</c>, is closed over every entity
/// type of the model that fits its constraints.
/// </para>
/// <para>
/// A handler declared here needs no registration of its own: it is created from the services of the saving context,
/// so its constructor is injected as usual. A host that registers the class in its services decides its lifetime.
/// A handler whose only constructor takes nothing needs no services at all: it is created with <c>new</c>, also on a
/// context that has none, and asks for what it needs through its rows (<see cref="RowSet{TEntity}.Services"/>).
/// </para>
/// <para>
/// <b>Instances.</b> A handler that is only declared here is a new instance for each stage call, disposed after that
/// call. One that the host registers (<c>AddSaveHandler</c>, which registers it as scoped) is one instance per scope,
/// across stages and saves. So a handler that passes something on, from one round, stage or save to a later one, keeps
/// it in <see cref="RowSet{TEntity}.Items"/>, not in its fields: then it behaves the same wherever it is declared.
/// </para>
/// </remarks>
public sealed class SaveHandlerSet
{
    private readonly List<SaveHandlerRegistration> declared = [];

    internal SaveHandlerSet() { }

    internal IReadOnlyList<SaveHandlerRegistration> Declared => declared;

    /// <summary>Declares a handler class.</summary>
    /// <param name="order">
    /// Its place among the handlers of a stage, lowest first. Among handlers with the same order, the context's run
    /// first, then the modules', then the host's, each in the order they were declared.
    /// </param>
    /// <typeparam name="THandler">The handler class.</typeparam>
    public SaveHandlerSet Add<THandler>(int order = 0) where THandler : class => Add(typeof(THandler), order);

    /// <summary>Declares a handler class, which may be an open generic class with one type parameter.</summary>
    /// <param name="handlerType">The handler class.</param>
    /// <param name="order"><inheritdoc cref="Add{THandler}(int)" path="/param[@name='order']"/></param>
    public SaveHandlerSet Add(Type handlerType, int order = 0)
    {
        SaveHandlerRegistration.Validate(handlerType, nameof(handlerType));

        if (!declared.Exists(registration => registration.HandlerType == handlerType))
            declared.Add(new SaveHandlerRegistration(handlerType, order));

        return this;
    }
}
