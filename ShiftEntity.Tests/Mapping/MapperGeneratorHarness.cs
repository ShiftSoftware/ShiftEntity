using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ShiftSoftware.ShiftEntity.Core;
using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Mapping;

/// <summary>
/// Drives <see cref="SourceGenerator.ShiftEntityMapperGenerator"/> over a hand-built compilation, and — this is
/// the part that matters — can EXECUTE what it generated.
/// <para>
/// Asserting on generated source TEXT only proves the generator emitted a certain string. A mapper that emits
/// the right substring and still produces the wrong object passes such a test. <see cref="Load"/> emits the
/// compilation to memory, loads it, and hands back real objects, so a test can say "run the mapper, then assert
/// the resulting object" instead.
/// </para>
/// <para>
/// Everything is reflection-based on purpose: the scaffold's types exist only in the assembly built at runtime,
/// so the test host cannot name them. Auto-generated mapper classes are <c>internal</c>, which also rules out
/// <c>dynamic</c> (the runtime binder honours accessibility). The wrappers below hide that.
/// </para>
/// </summary>
internal static class MapperGeneratorHarness
{
    /// <summary>Generator output for one scaffold: what it reported, and what it wrote.</summary>
    internal sealed record GeneratorRun(
        ImmutableArray<Diagnostic> Diagnostics,
        List<(string Name, string Text)> Sources)
    {
        /// <summary>The single generated source whose hint name contains <paramref name="namePart"/>.</summary>
        internal string Source(string namePart) =>
            Sources.Single(s => s.Name.Contains(namePart, StringComparison.Ordinal)).Text;

        internal IEnumerable<Diagnostic> OfId(string id) => Diagnostics.Where(d => d.Id == id);
    }

    /// <summary>
    /// Runs the generator and returns its diagnostics plus its sources, without compiling the result.
    /// Use when the diagnostic IS the subject (its firing and — just as important — its silence).
    /// </summary>
    internal static GeneratorRun Run(string source)
    {
        var compilation = CreateCompilation(source);

        var driver = CSharpGeneratorDriver
            .Create(new SourceGenerator.ShiftEntityMapperGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        var sources = driver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToList();

        return new GeneratorRun(diagnostics, sources);
    }

    /// <summary>
    /// Runs the generator, compiles scaffold + generated code together, loads the result, and returns a handle
    /// for creating objects and invoking mappers. A mapper that does not compile fails here, loudly.
    /// </summary>
    internal static GeneratedAssembly Load(string source)
    {
        var compilation = CreateCompilation(source);

        CSharpGeneratorDriver
            .Create(new SourceGenerator.ShiftEntityMapperGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var emitErrors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(emitErrors.Count == 0,
            "Generated mappers do not compile:" + Environment.NewLine + string.Join(Environment.NewLine, emitErrors));

        using var peStream = new MemoryStream();
        var result = output.Emit(peStream);

        Assert.True(result.Success,
            "Emit failed:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        // Loaded into the default context: the generated code calls into the SAME ShiftEntity.Core this test
        // host references (MappingHelpers, ShiftMapperBuilder, the registry), so it must resolve to the very
        // assemblies already loaded here, not to second copies.
        return new GeneratedAssembly(Assembly.Load(peStream.ToArray()));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        // A distinct assembly name per scaffold. Several tests load their compilation into this process, and two
        // loaded assemblies sharing one identity make for confusing type-identity failures.
        var compilation = CSharpCompilation.Create(
            $"ShiftEntity.MapperGeneratorHarness.Sample_{(uint)source.GetHashCode():x8}",
            // A file path, because a real compilation has one: without it every diagnostic location comes back
            // with an empty path, and a test asserting that a warning is navigable cannot tell that apart from
            // the bug it is guarding against.
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Scaffold.cs")],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // A scaffold that doesn't compile resolves no Shift types, so the generator stays silent and every
        // "assert this diagnostic does NOT fire" test would pass for the wrong reason. Fail loudly instead.
        //
        // Two errors are expected here and must NOT fail the check: a [ShiftEntityMapper] partial declares the
        // mapper interface and a `partial void Configure`, and the generator supplies the other half. Before it
        // runs, that scaffold is legitimately incomplete. Everything else — a mistyped member, a missing ID
        // override — is a broken scaffold and still fails.
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => d.Id is not ("CS0535" or "CS0759"))
            .ToList();

        Assert.True(errors.Count == 0,
            "Test scaffold does not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

        return compilation;
    }

    /// <summary>
    /// Everything this test host was built against — the framework, EF Core and the ShiftEntity assemblies the
    /// scaffold names. Deduped by simple name because the TPA list can carry a package and framework copy of one
    /// assembly, which Roslyn rejects as an ambiguous reference.
    /// </summary>
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.First()))
            .ToArray();
}

/// <summary>A loaded scaffold: makes its types, and runs the mappers the generator wrote for them.</summary>
internal sealed class GeneratedAssembly(Assembly assembly)
{
    /// <summary>A scaffold type by full name — e.g. <c>Sample.Schedule</c>.</summary>
    internal Type Type(string fullName) =>
        assembly.GetType(fullName) ?? throw new InvalidOperationException(
            $"Type '{fullName}' not found. Available: {string.Join(", ", assembly.GetTypes().Select(t => t.FullName))}");

    /// <summary>A new instance of a scaffold type, with optional property values.</summary>
    internal object New(string fullName, params (string Property, object? Value)[] values)
    {
        var instance = Activator.CreateInstance(Type(fullName))!;

        foreach (var (property, value) in values)
            Set(instance, property, value);

        return instance;
    }

    /// <summary>A <c>List&lt;T&gt;</c> of a scaffold type, as an <see cref="IQueryable"/> for MapToList.</summary>
    internal IQueryable Queryable(string fullName, params object[] items)
    {
        var elementType = Type(fullName);
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;

        foreach (var item in items)
            list.Add(item);

        return (IQueryable)typeof(Queryable)
            .GetMethods()
            .Single(m => m.Name == nameof(System.Linq.Queryable.AsQueryable) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(elementType)
            .Invoke(null, [list])!;
    }

    /// <summary>
    /// The generated mapper whose type name contains <paramref name="namePart"/>, ready to call. Auto mappers are
    /// named <c>Generated_{Entity}_{ListDTO}_{ViewDTO}_{hash}</c>; a declared partial keeps its own name.
    /// </summary>
    internal Mapper Mapper(string namePart)
    {
        // Each mapper is emitted alongside a "{name}Registration" module-initializer class, whose name contains
        // the mapper's — skip it, or every lookup here is ambiguous.
        var type = assembly.GetTypes().Single(t =>
            t.Name.Contains(namePart, StringComparison.Ordinal) &&
            !t.Name.EndsWith("Registration", StringComparison.Ordinal));

        return new Mapper(Activator.CreateInstance(type, nonPublic: true)!, type);
    }

    /// <summary>A static property value off a scaffold type.</summary>
    internal object? GetStatic(string fullName, string property) =>
        (Type(fullName).GetProperty(property, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
         ?? throw new InvalidOperationException($"No static property '{property}' on {fullName}"))
        .GetValue(null);

    internal static object? Get(object instance, string property) =>
        (instance.GetType().GetProperty(property)
         ?? throw new InvalidOperationException($"No property '{property}' on {instance.GetType().Name}"))
        .GetValue(instance);

    /// <summary>A property value, cast for you — <c>Get&lt;string&gt;(dto, "Name")</c>.</summary>
    internal static T? Get<T>(object instance, string property) => (T?)Get(instance, property);

    internal static void Set(object instance, string property, object? value) =>
        (instance.GetType().GetProperty(property)
         ?? throw new InvalidOperationException($"No property '{property}' on {instance.GetType().Name}"))
        .SetValue(instance, value);

    /// <summary>The items of a collection-typed property, as objects.</summary>
    internal static List<object> Items(object instance, string property) =>
        ((IEnumerable?)Get(instance, property))?.Cast<object>().ToList() ?? [];
}

/// <summary>
/// A generated mapper instance. Calls go through the four <see cref="IShiftEntityMapper{TEntity, TListDTO, TViewDTO}"/>
/// methods by reflection, because the entity and DTO types only exist in the generated assembly.
/// </summary>
internal sealed class Mapper(object instance, Type type)
{
    internal object Instance => instance;

    /// <summary>
    /// The <c>ShiftMapperBuilder&lt;E, L, V&gt;</c> this mapper is configured through, read off its
    /// <c>IShiftMapperConfigurable</c> interface. Needed to build a configuration delegate at runtime.
    /// </summary>
    internal Type BuilderType
    {
        get
        {
            var configurable = type.GetInterfaces()
                .Single(i => i.Name.StartsWith("IShiftMapperConfigurable", StringComparison.Ordinal));

            return typeof(ShiftMapperBuilder<,,>).MakeGenericType(configurable.GetGenericArguments());
        }
    }

    internal object MapToView(object entity, MappingContext context = default) =>
        Invoke(nameof(MapToView), entity, context);

    internal object MapToEntity(object dto, object existing, MappingContext context = default) =>
        Invoke(nameof(MapToEntity), dto, existing, context);

    /// <summary>Runs the list projection and materializes it — the projection is the thing under test.</summary>
    internal List<object> MapToList(IQueryable query, MappingContext context = default) =>
        ((IEnumerable)Invoke(nameof(MapToList), query, context)).Cast<object>().ToList();

    internal void CopyEntity(object source, object target, MappingContext context = default) =>
        Invoke(nameof(CopyEntity), source, target, context);

    /// <summary>Applies fluent configuration the way a repository's <c>UseGeneratedMapper(map => …)</c> would.</summary>
    internal void AddConfiguration(object configure)
    {
        var method = type.GetInterfaces()
            .Single(i => i.Name.StartsWith("IShiftMapperConfigurable", StringComparison.Ordinal))
            .GetMethod("AddConfiguration")!;

        try
        {
            method.Invoke(instance, [configure]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // What VerifyBaked threw is the assertion — do not make every test unwrap reflection's wrapper.
            throw ex.InnerException;
        }
    }

    private object Invoke(string method, params object?[] args)
    {
        var target = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No method '{method}' on {type.Name}");

        try
        {
            return target.Invoke(instance, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface what the mapper actually threw — the exception IS the assertion in the conversion tests.
            throw ex.InnerException;
        }
    }
}
