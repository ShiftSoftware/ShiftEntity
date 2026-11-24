using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Analyzers;

/// <summary>
/// Warns about a save handler that runs inside the save's transaction and is given a way to call another system.
/// <para>
/// <c>IAfterSaveHandler&lt;T&gt;</c> and <c>IBeforeCommitHandler&lt;T&gt;</c> run before the commit. A call to another
/// system made there holds the transaction open while it waits, tells that system about rows that may still roll
/// back, and is not undone when they do. The stage for it is <c>IAfterCommitHandler&lt;T&gt;</c>. The mistake is easy
/// to make when a trigger of the old library is ported, because its "after save" ran after the commit and the
/// handler with the same words in its name does not.
/// </para>
/// <para>
/// The rule looks at what the handler's constructor takes: an HTTP client or its factory, a Service Bus client or
/// sender, or a type whose name says that it sends (<c>...Sender</c>). It is a warning and not an error: it reads
/// names, so it can be wrong, and a handler may have a reason. A class that also implements
/// <c>IAfterCommitHandler&lt;T&gt;</c> is left alone, because it may use what it takes only there.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SaveHandlerAnalyzer : DiagnosticAnalyzer
{
    public const string CallsAnotherSystemInsideTheTransactionId = "SHENT002";

    public static readonly DiagnosticDescriptor CallsAnotherSystemInsideTheTransaction = new(
        CallsAnotherSystemInsideTheTransactionId,
        "A save handler that runs inside the transaction takes a client of another system",
        "'{0}' is {1}, which runs inside the save's transaction, and its constructor takes '{2}' ({3}). A call to another " +
        "system made there happens before the commit: it holds the transaction open while it waits, and it is not undone " +
        "when the transaction rolls back. Make the call from an IAfterCommitHandler<T>, which runs once the rows are committed.",
        "ShiftEntity",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "IAfterSaveHandler<T> and IBeforeCommitHandler<T> are for writes that must succeed or fail with the save. " +
            "Telling another system (an HTTP call, a message, an email) belongs in IAfterCommitHandler<T>.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(CallsAnotherSystemInsideTheTransaction);

    private const string HandlerNamespace = "ShiftSoftware.ShiftEntity.EFCore";

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var cls = (INamedTypeSymbol)context.Symbol;

        if (cls.TypeKind != TypeKind.Class)
            return;

        var afterSave = false;
        var beforeCommit = false;

        foreach (var implemented in cls.AllInterfaces)
        {
            if (implemented.Arity != 1 || implemented.ContainingNamespace?.ToDisplayString() != HandlerNamespace)
                continue;

            switch (implemented.Name)
            {
                case "IAfterSaveHandler":
                    afterSave = true;
                    break;
                case "IBeforeCommitHandler":
                    beforeCommit = true;
                    break;
                case "IAfterCommitHandler":
                    // It may use what it takes only once the rows are committed.
                    return;
            }
        }

        if (!afterSave && !beforeCommit)
            return;

        var stage = afterSave && beforeCommit ? "an after-save and a before-commit handler"
            : afterSave ? "an after-save handler"
            : "a before-commit handler";

        foreach (var constructor in cls.InstanceConstructors)
        {
            if (constructor.IsImplicitlyDeclared)
                continue;

            foreach (var parameter in constructor.Parameters)
            {
                if (WhatItCalls(parameter.Type) is not { } what)
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    CallsAnotherSystemInsideTheTransaction,
                    parameter.Locations.FirstOrDefault() ?? cls.Locations.FirstOrDefault(),
                    cls.Name, stage, parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), what));
            }
        }
    }

    /// <summary>What a constructor parameter of this type lets its class call, or null when it is nothing of the kind.</summary>
    private static string? WhatItCalls(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var name = current.Name;
            var ns = current.ContainingNamespace?.ToDisplayString() ?? "";

            if (ns == "System.Net.Http" && name is "HttpClient" or "HttpMessageInvoker" or "IHttpClientFactory")
                return "an HTTP client";

            if (ns.StartsWith("Azure.Messaging.ServiceBus", StringComparison.Ordinal) || ns.StartsWith("Microsoft.Azure.ServiceBus", StringComparison.Ordinal))
                return "a Service Bus client";
        }

        if (type.AllInterfaces.Any(implemented => implemented.Name == "IHttpClientFactory" && implemented.ContainingNamespace?.ToDisplayString() == "System.Net.Http"))
            return "an HTTP client";

        // A name that says what the type does: IEmailSender, ISmsSender, MessageSender.
        if (type.Name.EndsWith("Sender", StringComparison.Ordinal) ||
            type.AllInterfaces.Any(implemented => implemented.Name.EndsWith("Sender", StringComparison.Ordinal)))
            return "a message sender";

        return null;
    }
}
