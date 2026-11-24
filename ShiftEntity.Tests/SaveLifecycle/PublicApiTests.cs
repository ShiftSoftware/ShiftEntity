using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.CosmosDbReplication;
using ShiftSoftware.ShiftEntity.CosmosDbReplication.Services;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.EFCore;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Web;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.SaveLifecycle;

/// <summary>
/// The public surface of the save lifecycle, read by reflection: the types an app sees, their members, what the
/// context and the registrations add, and the names that are gone. A member that becomes public by accident, or a
/// removed name that comes back, fails here.
/// </summary>
public class PublicApiTests
{
    private static readonly Assembly EfCore = typeof(ShiftDbContext).Assembly;

    private static readonly Assembly[] Framework =
    [
        typeof(ShiftEntity<>).Assembly,                     // ShiftEntity.Core
        typeof(ShiftEntityResponse).Assembly,               // ShiftEntity.Model
        EfCore,                                             // ShiftEntity.EFCore
        typeof(ShiftEntityCrudHandler<,,,>).Assembly,       // ShiftEntity.Web
        typeof(CosmosDBReplication).Assembly,               // ShiftEntity.CosmosDbReplication
    ];

    // ── How a member reads ──────────────────────────────────────────────────────

    private static string Name(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name;

        if (type.IsByRef)
            return "ref " + Name(type.GetElementType()!);

        if (type.IsArray)
            return Name(type.GetElementType()!) + "[]";

        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return Name(underlying) + "?";

        var name = type.Name;

        if (!type.IsGenericType)
            return name switch { "Void" => "void", "Int32" => "int", "Boolean" => "bool", "Object" => "object", "String" => "string", _ => name };

        var tick = name.IndexOf('`');

        return $"{(tick < 0 ? name : name[..tick])}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }

    private static string Parameters(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(parameter =>
            (parameter.GetCustomAttribute<ParamArrayAttribute>() is null ? "" : "params ") + Name(parameter.ParameterType) + (parameter.HasDefaultValue ? " = default" : "")));

    private static string Describe(MethodInfo method) =>
        $"{Name(method.ReturnType)} {method.Name}{(method.IsGenericMethodDefinition ? $"<{string.Join(", ", method.GetGenericArguments().Select(Name))}>" : "")}({Parameters(method)})";

    private static bool IsVisible(MethodBase? method) => method is { IsPublic: true } or { IsFamily: true } or { IsFamilyOrAssembly: true };

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Every member of a type that code outside the framework can see, declared by the type itself.</summary>
    private static List<string> MembersOf(Type type)
    {
        var members = new List<string>();

        foreach (var constructor in type.GetConstructors(Declared).Where(IsVisible))
            members.Add($"new({Parameters(constructor)})");

        foreach (var property in type.GetProperties(Declared))
        {
            var get = IsVisible(property.GetMethod);
            var set = IsVisible(property.SetMethod);

            if (get || set)
                members.Add($"{Name(property.PropertyType)} {(property.GetIndexParameters().Length > 0 ? "this[]" : property.Name)} {{ {(get ? "get; " : "")}{(set ? "set; " : "")}}}");
        }

        foreach (var method in type.GetMethods(Declared).Where(method => IsVisible(method) && !method.IsSpecialName))
        {
            // What the compiler writes for a record or an explicit interface implementation is not the API.
            if (method.GetCustomAttribute<CompilerGeneratedAttribute>() is not null || method.Name.Contains('.'))
                continue;

            members.Add(Describe(method));
        }

        foreach (var field in type.GetFields(Declared).Where(field => (field.IsPublic || field.IsFamily) && !field.IsSpecialName))
            members.Add(type.IsEnum ? $"{field.Name} = {Convert.ToInt32(field.GetRawConstantValue())}" : $"{Name(field.FieldType)} {field.Name}");

        members.Sort(StringComparer.Ordinal);
        return members;
    }

    private static void Has(Type type, params string[] members) =>
        Assert.Equal(members.Order(StringComparer.Ordinal), MembersOf(type));

    // ── The types ───────────────────────────────────────────────────────────────

    private static readonly Type[] Types =
    [
        typeof(IBeforeSaveHandler<>), typeof(IAfterSaveHandler<>), typeof(IBeforeCommitHandler<>), typeof(IAfterCommitHandler<>),
        typeof(NeedsOriginalAttribute), typeof(GuardAttribute),
        typeof(RowChange), typeof(ChangedRow<>), typeof(RowSet<>), typeof(PendingRows<>), typeof(SavedRows<>), typeof(HandlerRows),
        typeof(SaveHandlerSet), typeof(SaveStages),
        typeof(ShiftDatabaseFacade), typeof(AfterCommitException),
    ];

    [Fact]
    public void The_save_lifecycle_has_these_public_types_and_no_other()
    {
        Assert.All(Types, type => Assert.True(type.IsPublic, $"{type.Name} is not public."));
        Assert.All(Types, type => Assert.Equal("ShiftSoftware.ShiftEntity.EFCore", type.Namespace));

        // Every public type of the framework that reads like a part of a save is one of the types above. One older
        // type shares a word with them and nothing else: the endpoints' handler. No public type has "Trigger" in its
        // name any more: the two builders of a replication registration lost it.
        Type[] older = [typeof(ShiftEntityCrudHandler<,,,>)];

        string[] words = ["Handler", "ChangedRow", "RowSet", "SavedRow", "PendingRow", "RowChange", "SaveStage", "SaveReport", "SaveObserver",
            "SaveUnit", "SaveHook", "SaveValidator", "Trigger", "AfterCommit", "BeforeCommit", "Guard", "NeedsOriginal", "DatabaseFacade", "Lifecycle"];

        var found = Framework.SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => words.Any(word => type.Name.Contains(word, StringComparison.Ordinal)))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Types.Concat(older).Select(type => type.Name).Order(StringComparer.Ordinal), found);
    }

    [Fact]
    public void The_handler_interfaces_have_one_method_each()
    {
        Has(typeof(IBeforeSaveHandler<>), "ValueTask BeforeSaveAsync(PendingRows<TEntity>, CancellationToken)");
        Has(typeof(IAfterSaveHandler<>), "ValueTask AfterSaveAsync(SavedRows<TEntity>, CancellationToken)");
        Has(typeof(IBeforeCommitHandler<>), "ValueTask BeforeCommitAsync(SavedRows<TEntity>, CancellationToken)");
        Has(typeof(IAfterCommitHandler<>), "ValueTask AfterCommitAsync(SavedRows<TEntity>, CancellationToken)");

        Has(typeof(NeedsOriginalAttribute), "new()");
        Has(typeof(GuardAttribute), "new()");

        Assert.Equal(AttributeTargets.Class, typeof(NeedsOriginalAttribute).GetCustomAttribute<AttributeUsageAttribute>()!.ValidOn);
        Assert.Equal(AttributeTargets.Class, typeof(GuardAttribute).GetCustomAttribute<AttributeUsageAttribute>()!.ValidOn);
        Assert.False(typeof(GuardAttribute).GetCustomAttribute<AttributeUsageAttribute>()!.Inherited);
    }

    [Fact]
    public void A_handler_is_given_rows_with_these_members()
    {
        Has(typeof(RowChange), "Added = 0", "Modified = 1", "Deleted = 2");

        Has(typeof(ChangedRow<>),
            "new(TEntity, RowChange, TEntity = default)",
            "TEntity Entity { get; }",
            "RowChange Change { get; }",
            "TEntity Original { get; }",
            "bool IsSoftDelete { get; }",
            "bool Became(Func<TEntity, bool>)");

        Has(typeof(RowSet<>),
            "ShiftDbContext Db { get; }",
            "IDictionary<object, object> Items { get; }",
            "IServiceProvider Services { get; }",
            "ValueTask TakeLockAsync(string, CancellationToken = default)",
            "int Count { get; }",
            "ChangedRow<TEntity> this[] { get; }",
            "IEnumerator<ChangedRow<TEntity>> GetEnumerator()");

        Has(typeof(PendingRows<>),
            "bool IsRepositorySave { get; }",
            "EntityEntry<TEntity> Entry(ChangedRow<TEntity>)",
            "ValueTask RequireTransactionAsync(CancellationToken = default)",
            "bool IsSwitchedOff(SaveStages)");

        Has(typeof(SavedRows<>));

        Has(typeof(HandlerRows),
            "PendingRows<TEntity> Pending<TEntity>(ShiftDbContext, params ChangedRow<TEntity>[])",
            "PendingRows<TEntity> Pending<TEntity>(ShiftDbContext, bool, params ChangedRow<TEntity>[])",
            "SavedRows<TEntity> Saved<TEntity>(ShiftDbContext, params ChangedRow<TEntity>[])",
            "SavedRows<TEntity> Saved<THandler, TEntity>(ShiftDbContext, params ChangedRow<TEntity>[])");

        // A row set is made by the framework, or by HandlerRows for a test: no class outside can derive from it.
        Assert.True(typeof(RowSet<>).IsAbstract);
        Assert.True(typeof(PendingRows<>).IsSealed);
        Assert.True(typeof(SavedRows<>).IsSealed);
    }

    [Fact]
    public void A_handler_is_declared_with_these_members()
    {
        Has(typeof(SaveHandlerSet),
            "SaveHandlerSet Add<THandler>(int = default)",
            "SaveHandlerSet Add(Type, int = default)");

        Has(typeof(SaveStages), "None = 0", "BeforeSave = 1", "AfterSave = 2", "BeforeCommit = 4", "AfterCommit = 8");
        Assert.NotNull(typeof(SaveStages).GetCustomAttribute<FlagsAttribute>());

        // A module declares its handlers next to its model. The method has a body of its own, so a module compiled
        // before it existed still loads.
        Has(typeof(IModelBuildingContributor),
            "void Configure(ModelBuilder)",
            "void ConfigureSaveHandlers(SaveHandlerSet)");

        Assert.False(typeof(IModelBuildingContributor).GetMethod(nameof(IModelBuildingContributor.ConfigureSaveHandlers))!.IsAbstract);

        // The host: two methods, in the namespace of the framework's other registrations.
        var registrations = Framework.SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Name == "AddSaveHandler")
            .ToList();

        Assert.Equal(
            ["IServiceCollection AddSaveHandler(IServiceCollection, Type, int = default)", "IServiceCollection AddSaveHandler<THandler>(IServiceCollection, int = default)"],
            registrations.Select(Describe).Order(StringComparer.Ordinal));

        Assert.All(registrations, method => Assert.Equal("Microsoft.Extensions.DependencyInjection", method.DeclaringType!.Namespace));
    }

    [Fact]
    public void The_context_has_these_members_for_a_save_and_no_other()
    {
        string[] words = ["Suppress", "AfterCommit", "AttachServices", "ConfigureSaveHandlers", "SaveChangesWithout", "Handler", "Trigger", "Database"];

        var members = MembersOf(typeof(ShiftDbContext)).Where(member => words.Any(word => member.Contains(word, StringComparison.Ordinal)));

        Assert.Equal(new[]
        {
            "IDisposable SuppressAfterCommit()",
            "IDisposable SuppressAllSaveHandlers()",
            "IDisposable SuppressAuditStamping()",
            "IDisposable SuppressNotifications()",
            "IDisposable SuppressReplication()",
            "IDisposable SuppressSaveHandler<THandler>()",
            "IDisposable SuppressSaveHandlers(SaveStages)",
            "ShiftDatabaseFacade Database { get; }",
            "void AfterCommit(Action)",
            "void AfterCommit(Func<CancellationToken, Task>)",
            "void AfterCommit(Func<Task>)",
            "void AttachServices(IServiceProvider)",
            "void ConfigureSaveHandlers(SaveHandlerSet)",
        }.Order(StringComparer.Ordinal), members);

        var configure = typeof(ShiftDbContext).GetMethod("ConfigureSaveHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(configure is { IsFamily: true, IsVirtual: true, IsAbstract: false });
    }

    [Fact]
    public void The_database_facade_and_the_exception_add_these_members()
    {
        Has(typeof(ShiftDatabaseFacade),
            "IDbContextTransaction BeginTransaction()",
            "IDbContextTransaction BeginTransaction(IsolationLevel)",
            "Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken = default)",
            "Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel, CancellationToken = default)",
            "void CommitTransaction()",
            "Task CommitTransactionAsync(CancellationToken = default)",
            "void RollbackTransaction()",
            "Task RollbackTransactionAsync(CancellationToken = default)",
            "Task TakeLockAsync(string, CancellationToken = default)",
            "IDbContextTransaction CurrentTransaction { get; }",
            "AutoTransactionBehavior AutoTransactionBehavior { get; set; }",
            "bool AutoSavepointsEnabled { get; set; }");

        Assert.True(typeof(ShiftDatabaseFacade).IsSealed);

        // An app builds one to test its own catch, and reads the message of the answer that reports the save as saved.
        Has(typeof(AfterCommitException),
            "new(IReadOnlyList<Exception>)",
            "IReadOnlyList<Exception> Failures { get; }",
            "Message SavedMessage(bool = default)");
        Assert.True(typeof(AfterCommitException).IsSealed);
    }

    [Fact]
    public void The_response_has_the_marker_of_a_save_whose_follow_up_failed()
    {
        Assert.Equal("SavedWithFailure", ShiftEntityResponse.SavedWithFailure);
    }

    [Fact]
    public void The_scans_at_start_list_an_assemblys_types_through_one_method()
    {
        // The one place the framework's scans list the types of an app's or a module's assembly, and which a module
        // such as ShiftIdentity calls for its own scans.
        var scan = typeof(AssemblyScan);

        Assert.True(scan is { IsPublic: true, IsAbstract: true, IsSealed: true });
        Assert.Equal("ShiftSoftware.ShiftEntity.Core", scan.Namespace);
        Assert.Same(typeof(ShiftEntity<>).Assembly, scan.Assembly);
        Has(scan, "Type[] TypesOf(Assembly)");
    }

    [Fact]
    public void Replication_on_save_is_registered_and_built_with_these_names()
    {
        var registration = typeof(CosmosDBReplication).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Name.StartsWith("AddShiftEntityCosmosDbReplication", StringComparison.Ordinal))
            .Select(Describe)
            .Order(StringComparer.Ordinal);

        // On save, and for a catch-up.
        Assert.Equal(
            ["IServiceCollection AddShiftEntityCosmosDbReplication<TDbContext>(IServiceCollection)",
             "IServiceCollection AddShiftEntityCosmosDbReplicationOnSave<TDbContext>(IServiceCollection, Action<ShiftEntityCosmosDbOptions>)"],
            registration);

        // What SetUpReplication hands back, and what its Replicate and ReplicateTree hand back.
        Assert.True(typeof(CosmosDbOnSaveReplicationOperation<>).IsPublic);
        Assert.True(typeof(CosmosDbOnSaveReferenceOperations<>).IsPublic);

        Assert.All(typeof(ShiftEntityCosmosDbOptions).GetMethods().Where(method => method.Name == "SetUpReplication"),
            method => Assert.Equal(typeof(CosmosDbOnSaveReplicationOperation<>), method.ReturnType.GetGenericTypeDefinition()));

        Assert.All(typeof(CosmosDbOnSaveReplicationOperation<>).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => Assert.Equal(typeof(CosmosDbOnSaveReferenceOperations<>), method.ReturnType.GetGenericTypeDefinition()));

        // The manual replication service builds with types of its own, which keep their names. The on-save builders
        // say "OnSave", so that the two pairs do not read alike.
        Assert.True(typeof(CosmosDbReplicationOperation<,>).IsPublic);
        Assert.True(typeof(CosmosDbReferenceOperation<,>).IsPublic);
    }

    // ── What is gone ────────────────────────────────────────────────────────────

    [Fact]
    public void The_removed_names_are_gone_and_what_became_internal_is_not_public()
    {
        var all = Framework.SelectMany(assembly => assembly.GetTypes()).ToList();
        var exported = Framework.SelectMany(assembly => assembly.GetExportedTypes()).ToList();

        string[] removedTypes =
        [
            "IShiftEntityHasBeforeSaveHook`1", "IShiftEntityHasAfterSaveHook`1", "IShiftEntitySaveValidator",
            "ISaveHandler`1", "SavedRow`1", "ShiftDbContextTransactionInterceptor",
            "CosmosDbTriggerReplicateOperation`1", "CosmosDbTriggerReferenceOperations`1",
            "CosmosDbReplicateOperation`1", "CosmosDbReferenceOperations`1",
        ];

        Assert.DoesNotContain(all, type => removedTypes.Contains(type.Name));

        string[] nowInternal = ["IShiftDbContextSaveObserver", "SaveReport", "SavedRow", "SavedRowChange"];

        Assert.DoesNotContain(exported, type => nowInternal.Contains(type.Name));
        Assert.Equal(nowInternal.Order(), all.Where(type => nowInternal.Contains(type.Name)).Select(type => type.Name).Order());

        string[] removedMethods =
        [
            "UseShiftEntityAfterCommit", "UseTriggersOn", "SaveChangesWithoutHandlers", "SaveChangesWithoutHandlersAsync",
            "SaveChangesWithoutTriggers", "SaveChangesWithoutTriggersAsync", "RunSaveValidators",
            "AddShiftEntityCosmosDbReplicationTrigger",
        ];

        var methods = all.SelectMany(type => type.GetMethods(Declared)).Select(method => method.Name).ToHashSet();

        Assert.DoesNotContain(removedMethods, methods.Contains);

        // A switch that names no stage is gone: the one that is left takes the stages.
        Assert.Single(typeof(ShiftDbContext).GetMethods().Where(method => method.Name == "SuppressSaveHandlers"));
    }

    [Fact]
    public void No_assembly_of_the_framework_references_the_trigger_library()
    {
        Assert.All(Framework, assembly => Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name!.StartsWith("EntityFrameworkCore.Triggered", StringComparison.OrdinalIgnoreCase)));
    }
}
