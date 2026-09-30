using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftSoftware.ShiftEntity.Tests.Localization.Scenario;

/// <summary>
/// One HTTP invocation of a function, built by hand: a FunctionContext with what the request localization middleware
/// and the worker's extension methods read. With the ASP.NET Core integration the request is an HttpContext, which
/// the worker's proxying middleware adds before any other middleware runs. The built-in HTTP model has only
/// HttpRequestData. <see cref="WriteResponseAsync"/> writes the result the way the proxying middleware does.
/// </summary>
public sealed class HttpInvocation
{
    // What executing an IActionResult or an IResult needs: MVC's result executors and JSON output formatter.
    private static readonly IServiceProvider services = new ServiceCollection()
        .AddLogging()
        .AddMvcCore()
        .Services
        .BuildServiceProvider();

    private readonly BindingsFeature bindings;

    /// <param name="acceptLanguage">The Accept-Language header, or null for a request without one.</param>
    /// <param name="aspNetCoreIntegration">false for the built-in HTTP model, which has no HttpContext.</param>
    /// <param name="outputBindings">
    /// The output bindings of the function. By default the return value, of type "http", as for a function that
    /// returns an IActionResult.
    /// </param>
    public HttpInvocation(string? acceptLanguage, bool aspNetCoreIntegration = true, params (string Name, string Type)[] outputBindings)
    {
        HttpContext = new DefaultHttpContext { RequestServices = services };
        HttpContext.Response.Body = new MemoryStream();

        if (acceptLanguage is not null)
            HttpContext.Request.Headers.AcceptLanguage = acceptLanguage;

        var context = new TestFunctionContext(outputBindings.Length == 0 ? [("$return", "http")] : outputBindings);

        bindings = BindingsFeature.Create();
        context.TestFeatures.Set(BindingsFeature.FeatureType, bindings);

        var headers = new HttpHeadersCollection(HttpContext.Request.Headers
            .Select(header => new KeyValuePair<string, string>(header.Key, header.Value.ToString())));

        context.TestFeatures.Set<IHttpRequestDataFeature>(new RequestDataFeature(new TestHttpRequestData(context, headers)));

        // The key under which the ASP.NET Core integration keeps the HttpContext (FunctionContext.GetHttpContext).
        if (aspNetCoreIntegration)
            context.Items["HttpRequestContext"] = HttpContext;

        Context = context;
    }

    public FunctionContext Context { get; }

    public HttpContext HttpContext { get; }

    /// <summary>The value the function returned, as the worker keeps it.</summary>
    public object? InvocationResult => bindings.Result;

    public object? OutputBinding(string name) => bindings.Outputs.GetValueOrDefault(name);

    /// <summary>
    /// What the worker's output-bindings middleware does for a return type with several outputs. It runs inside every
    /// other middleware, and it moves each property of the returned object into its output binding.
    /// </summary>
    public void SetOutputBinding(string name, object? value) => bindings.Outputs[name] = value;

    /// <summary>
    /// What the worker's proxying middleware does when every other middleware has returned. It writes the IActionResult
    /// or IResult the function returned, else the one in the output binding of type "http". It runs in the flow of its
    /// caller, which has not seen the culture or the language of the request.
    /// </summary>
    public async Task<JsonElement> WriteResponseAsync()
    {
        var result = bindings.Result is IActionResult or IResult
            ? bindings.Result
            : Context.FunctionDefinition.OutputBindings
                .Where(binding => string.Equals(binding.Value.Type, "http", StringComparison.OrdinalIgnoreCase))
                .Select(binding => OutputBinding(binding.Key))
                .FirstOrDefault();

        switch (result)
        {
            case IActionResult actionResult:
                await actionResult.ExecuteResultAsync(new ActionContext(HttpContext, new RouteData(), new ActionDescriptor()));
                break;
            case IResult httpResult:
                await httpResult.ExecuteAsync(HttpContext);
                break;
            default:
                throw new InvalidOperationException("The function left no HTTP result to write.");
        }

        HttpContext.Response.Body.Position = 0;

        using var body = await JsonDocument.ParseAsync(HttpContext.Response.Body);

        return body.RootElement.Clone();
    }

    /// <summary>
    /// The worker keeps the invocation result and the output bindings behind an internal interface,
    /// IFunctionBindingsFeature. Its public extension methods (GetInvocationResult, GetOutputBindings) find it in
    /// FunctionContext.Features, so the scenario builds one with a DispatchProxy.
    /// </summary>
    public class BindingsFeature : DispatchProxy
    {
        internal static readonly Type FeatureType = typeof(FunctionContext).Assembly.GetType(
            "Microsoft.Azure.Functions.Worker.Context.Features.IFunctionBindingsFeature", throwOnError: true)!;

        internal object? Result;

        internal readonly Dictionary<string, object?> Outputs = new();

        internal static BindingsFeature Create() =>
            (BindingsFeature)DispatchProxy.Create(FeatureType, typeof(BindingsFeature));

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_InvocationResult":
                    return Result;
                case "set_InvocationResult":
                    Result = args![0];
                    return null;
                case "get_OutputBindingData":
                    return Outputs;
                default:
                    throw new NotSupportedException($"The scenario does not implement {targetMethod?.Name}.");
            }
        }
    }

    private sealed class TestFunctionContext((string Name, string Type)[] outputBindings) : FunctionContext
    {
        public TestFeatures TestFeatures { get; } = new();

        public override string InvocationId { get; } = Guid.NewGuid().ToString();

        public override string FunctionId => "localization";

        public override TraceContext TraceContext => throw new NotSupportedException();

        public override BindingContext BindingContext => throw new NotSupportedException();

        public override RetryContext RetryContext => throw new NotSupportedException();

        public override IServiceProvider InstanceServices { get; set; } = services;

        public override FunctionDefinition FunctionDefinition { get; } = new TestFunctionDefinition(outputBindings);

        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

        public override IInvocationFeatures Features => TestFeatures;
    }

    private sealed class TestFeatures : IInvocationFeatures
    {
        private readonly Dictionary<Type, object> features = new();

        public void Set<T>(T instance) => features[typeof(T)] = instance!;

        public void Set(Type type, object instance) => features[type] = instance;

        public T? Get<T>() => features.TryGetValue(typeof(T), out var feature) ? (T)feature : default;

        public IEnumerator<KeyValuePair<Type, object>> GetEnumerator() => features.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TestFunctionDefinition((string Name, string Type)[] outputBindings) : FunctionDefinition
    {
        public override ImmutableArray<FunctionParameter> Parameters => [];

        public override string PathToAssembly => typeof(HttpInvocation).Assembly.Location;

        public override string EntryPoint => $"{typeof(HttpInvocation).FullName}.Run";

        public override string Id => "localization";

        public override string Name => "localization";

        public override IImmutableDictionary<string, BindingMetadata> InputBindings { get; } =
            ImmutableDictionary<string, BindingMetadata>.Empty.Add("req", new TestBindingMetadata("req", "httpTrigger", BindingDirection.In));

        public override IImmutableDictionary<string, BindingMetadata> OutputBindings { get; } =
            outputBindings.ToImmutableDictionary(
                binding => binding.Name,
                binding => (BindingMetadata)new TestBindingMetadata(binding.Name, binding.Type, BindingDirection.Out));
    }

    private sealed class TestBindingMetadata(string name, string type, BindingDirection direction) : BindingMetadata
    {
        public override string Name => name;

        public override string Type => type;

        public override BindingDirection Direction => direction;
    }

    private sealed class RequestDataFeature(HttpRequestData request) : IHttpRequestDataFeature
    {
        public ValueTask<HttpRequestData?> GetHttpRequestDataAsync(FunctionContext context) => ValueTask.FromResult<HttpRequestData?>(request);
    }

    private sealed class TestHttpRequestData(FunctionContext context, HttpHeadersCollection headers) : HttpRequestData(context)
    {
        public override Stream Body => Stream.Null;

        public override HttpHeadersCollection Headers => headers;

        public override IReadOnlyCollection<IHttpCookie> Cookies => [];

        public override Uri Url => new("http://localhost/api/localization");

        public override IEnumerable<ClaimsIdentity> Identities => [];

        public override string Method => "GET";

        public override HttpResponseData CreateResponse() => throw new NotSupportedException();
    }
}
