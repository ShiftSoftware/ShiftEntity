using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using ShiftSoftware.ShiftEntity.Analyzers;
using System.Collections.Immutable;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Analyzers;

/// <summary>
/// Drives <see cref="SaveHandlerAnalyzer"/> over a hand-built compilation and pins what it reports.
/// <para>
/// SHENT002: a handler that runs inside the save's transaction (after save, before commit) whose constructor takes a
/// client of another system. A call made there happens before the commit and is not undone by a rollback, so it
/// belongs in an after-commit handler. It is a warning, because the rule reads the names of types. The cases where it
/// stays silent are as much its contract as the ones where it speaks.
/// </para>
/// </summary>
public class SaveHandlerAnalyzerTests
{
    private const string Id = SaveHandlerAnalyzer.CallsAnotherSystemInsideTheTransactionId;

    /// <summary>The fixed half of every case. <c>{{HANDLER}}</c> is the half under test.</summary>
    private const string Scaffold = """
        using System.Net.Http;
        using System.Threading;
        using System.Threading.Tasks;
        using Azure.Messaging.ServiceBus;
        using ShiftSoftware.ShiftEntity.Core;
        using ShiftSoftware.ShiftEntity.EFCore;

        // Stand-ins for the Service Bus package, which this test project does not reference. The rule goes by namespace.
        namespace Azure.Messaging.ServiceBus
        {
            public class ServiceBusClient { }
            public class ServiceBusSender { }
        }

        namespace Sample
        {
            public class Order : ShiftEntity<Order>
            {
                public string Name { get; set; } = "";
            }

            public interface IEmailSender { }
            public interface IOrderRules { }
            public class PricingApiClient : HttpClient { }

            {{HANDLER}}
        }
        """;

    private const string AfterSave = "public ValueTask AfterSaveAsync(SavedRows<Order> rows, CancellationToken cancellationToken) => default;";
    private const string BeforeCommit = "public ValueTask BeforeCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken) => default;";
    private const string AfterCommit = "public ValueTask AfterCommitAsync(SavedRows<Order> rows, CancellationToken cancellationToken) => default;";
    private const string BeforeSave = "public ValueTask BeforeSaveAsync(PendingRows<Order> rows, CancellationToken cancellationToken) => default;";

    // ── Where it speaks ─────────────────────────────────────────────────────────

    [Fact]
    public void An_after_save_handler_that_takes_an_HTTP_client_is_warned_about_at_the_parameter()
    {
        var diagnostic = Assert.Single(Run($$"""
            public class TellsTheWarehouse : IAfterSaveHandler<Order>
            {
                public TellsTheWarehouse(HttpClient warehouse) { }
                {{AfterSave}}
            }
            """));

        Assert.Equal(Id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("warehouse", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));

        var message = diagnostic.GetMessage();

        Assert.Contains("'TellsTheWarehouse' is an after-save handler, which runs inside the save's transaction", message);
        Assert.Contains("takes 'HttpClient' (an HTTP client)", message);
        Assert.Contains("Make the call from an IAfterCommitHandler<T>", message);
    }

    [Theory]
    [InlineData("IHttpClientFactory", "an HTTP client")]
    [InlineData("PricingApiClient", "an HTTP client")]
    [InlineData("ServiceBusClient", "a Service Bus client")]
    [InlineData("ServiceBusSender", "a Service Bus client")]
    [InlineData("IEmailSender", "a message sender")]
    public void A_before_commit_handler_is_warned_about_for_each_kind_of_client(string type, string what)
    {
        var diagnostic = Assert.Single(Run($$"""
            public class Totals : IBeforeCommitHandler<Order>
            {
                public Totals({{type}} other, IOrderRules rules) { }
                {{BeforeCommit}}
            }
            """));

        Assert.Contains("'Totals' is a before-commit handler", diagnostic.GetMessage());
        Assert.Contains($"takes '{type}' ({what})", diagnostic.GetMessage());
    }

    [Fact]
    public void A_primary_constructor_an_open_generic_class_and_a_class_with_both_stages_are_handlers_like_any_other()
    {
        Assert.Equal(Id, Assert.Single(Run($$"""
            public class Primary(HttpClient http) : IAfterSaveHandler<Order>
            {
                {{AfterSave}}
            }
            """)).Id);

        Assert.Equal(Id, Assert.Single(Run("""
            public class Audit<TEntity>(IEmailSender email) : IAfterSaveHandler<TEntity> where TEntity : class
            {
                public ValueTask AfterSaveAsync(SavedRows<TEntity> rows, CancellationToken cancellationToken) => default;
            }
            """)).Id);

        Assert.Contains("is an after-save and a before-commit handler", Assert.Single(Run($$"""
            public class Both(HttpClient http) : IAfterSaveHandler<Order>, IBeforeCommitHandler<Order>
            {
                {{AfterSave}}
                {{BeforeCommit}}
            }
            """)).GetMessage());
    }

    [Fact]
    public void Each_parameter_of_each_constructor_is_reported()
    {
        var diagnostics = Run($$"""
            public class Busy : IAfterSaveHandler<Order>
            {
                public Busy(HttpClient http, IEmailSender email) { }
                public Busy(ServiceBusSender queue) { }
                {{AfterSave}}
            }
            """);

        Assert.Equal(3, diagnostics.Length);
    }

    // ── Where it stays silent ───────────────────────────────────────────────────

    [Fact]
    public void An_after_commit_handler_may_take_any_client()
    {
        AssertSilent($$"""
            public class TellsTheWarehouse(HttpClient warehouse, ServiceBusSender queue, IEmailSender email) : IAfterCommitHandler<Order>
            {
                {{AfterCommit}}
            }
            """);
    }

    [Fact]
    public void A_class_that_also_has_an_after_commit_stage_is_left_alone()
    {
        // It may use the client only once the rows are committed. Telling the two apart would mean reading its code.
        AssertSilent($$"""
            public class WritesThenTells(HttpClient warehouse) : IAfterSaveHandler<Order>, IAfterCommitHandler<Order>
            {
                {{AfterSave}}
                {{AfterCommit}}
            }
            """);
    }

    [Fact]
    public void A_before_save_handler_is_not_this_rules_business()
    {
        AssertSilent($$"""
            public class Checks(HttpClient rates) : IBeforeSaveHandler<Order>
            {
                {{BeforeSave}}
            }
            """);
    }

    [Fact]
    public void A_handler_inside_the_transaction_that_takes_nothing_of_the_kind_is_fine()
    {
        AssertSilent($$"""
            public class Totals(ShiftDbContext db, IOrderRules rules) : IAfterSaveHandler<Order>, IBeforeCommitHandler<Order>
            {
                {{AfterSave}}
                {{BeforeCommit}}
            }

            public class NoConstructor : IAfterSaveHandler<Order>
            {
                {{AfterSave}}
            }
            """);
    }

    [Fact]
    public void A_class_that_is_no_handler_may_take_what_it_likes()
    {
        AssertSilent("""
            public class WarehouseApi(HttpClient http, IEmailSender email) { }
            """);
    }

    private static void AssertSilent(string handler) => Assert.Empty(Run(handler));

    // ── The compilation ─────────────────────────────────────────────────────────

    private static ImmutableArray<Diagnostic> Run(string handler)
    {
        var source = Scaffold.Replace("{{HANDLER}}", handler);

        var compilation = CSharpCompilation.Create(
            "ShiftEntity.AnalyzerTests.Handlers",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        // A scaffold that doesn't compile resolves no handler interface, so the analyzer would stay silent and every
        // silent case would pass for the wrong reason.
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();

        Assert.True(errors.Count == 0, "Test scaffold does not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

        return compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SaveHandlerAnalyzer()))
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Everything this test host was built against. Deduped by simple name, because the list can carry a package and
    /// a framework copy of one assembly, which Roslyn rejects as an ambiguous reference.
    /// </summary>
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(group => (MetadataReference)MetadataReference.CreateFromFile(group.First()))
            .ToArray();
}
