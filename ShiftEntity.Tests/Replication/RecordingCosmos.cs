using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using NSubstitute;

namespace ShiftSoftware.ShiftEntity.Tests.Replication;

/// <summary>
/// A Cosmos DB stand-in for replication tests. It records every write it is asked to do (upserts, patches and
/// deletes), in order, keeps the documents those writes leave, and answers the queries that reference updates run.
/// A test can make it refuse some writes.
/// </summary>
/// <remarks>
/// Writes go to an NSubstitute container, so nothing leaves the process. Queries need more than a substitute: a
/// reference update builds a LINQ query and runs it with <c>ToFeedIterator</c>, which works only on the SDK's own
/// query type. So each container hands out the LINQ queryable of a real <see cref="CosmosClient"/>, and that client
/// talks to <see cref="QueryGateway"/>, an in-process HTTP handler that answers the few gateway calls a query makes.
/// The gateway records the SQL text of each query, which shows which documents an update looks for. A query is
/// answered from <see cref="Answer"/> when the test set one for its container, and otherwise from the stored
/// documents, for the filters reference updates use: equalities joined with AND. A write that names a partition key
/// with another number of components than the container's is refused, as Cosmos DB refuses it.
/// <para>
/// ShiftIdentity.Tests has a copy of this file (Infrastructure/RecordingCosmos.cs). Keep the two the same.
/// </para>
/// </remarks>
public sealed class RecordingCosmos : IDisposable
{
    /// <summary>One write, as the replication code asked for it.</summary>
    /// <param name="Kind"><c>upsert</c>, <c>patch</c> or <c>delete</c>.</param>
    /// <param name="Container">The container id.</param>
    /// <param name="Id">The document id. For an upsert, the <c>id</c> member of the document.</param>
    /// <param name="PartitionKey">The partition key the call named, as JSON, or null when the call named none.</param>
    /// <param name="Body">The document of an upsert, or the operations of a patch. Null for a delete.</param>
    public sealed record Write(string Kind, string Container, string Id, string? PartitionKey, JToken? Body)
    {
        /// <summary>One line that a test can compare as text.</summary>
        public override string ToString() =>
            $"{Kind} {Container}/{Id}{(PartitionKey is null ? "" : " " + PartitionKey)}" +
            (Body is null ? "" : " " + Body.ToString(Newtonsoft.Json.Formatting.None));
    }

    private readonly string database;
    private readonly Dictionary<string, RecordingContainer> containers = new();
    private readonly QueryGateway gateway;
    private readonly CosmosClient queryClient;
    private readonly ConcurrentQueue<Write> writes = new();

    // The documents the accepted writes left, per container, keyed by id and partition key.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JObject>> store = new();

    /// <summary>The client to give to <c>SetUpReplication</c> or to a catch-up <c>SetUp</c>.</summary>
    public CosmosClient Client { get; }

    /// <summary>Every write so far, in the order the replication code made them.</summary>
    public IReadOnlyList<Write> Writes => writes.ToList();

    /// <summary>Every query so far, as (container, SQL text), in order.</summary>
    public IReadOnlyList<(string Container, string Query)> Queries => gateway.Queries.ToList();

    /// <summary>
    /// Decides whether a write is refused. A refused write throws a <see cref="CosmosException"/> with status 503,
    /// as the SDK does when the service refuses a request. A test can replace it at any time.
    /// </summary>
    public Func<Write, bool> Refuse { get; set; } = _ => false;

    public RecordingCosmos(string database = "pins")
    {
        this.database = database;
        gateway = new QueryGateway(QueryStore);

        // The account key only has to be valid base64: the gateway never checks the signature.
        queryClient = new CosmosClient("https://localhost:8081/", Convert.ToBase64String(new byte[64]), new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            HttpClientFactory = () => new HttpClient(gateway, disposeHandler: false),
        });

        var databaseStandIn = Substitute.For<Database>();
        databaseStandIn.GetContainer(Arg.Any<string>()).Returns(call =>
        {
            var id = call.Arg<string>();
            lock (containers)
                return containers.TryGetValue(id, out var container)
                    ? container
                    : throw new InvalidOperationException($"The test declared no container '{id}'.");
        });

        Client = Substitute.For<CosmosClient>();
        Client.ClientOptions.Returns(new CosmosClientOptions());
        Client.GetDatabase(database).Returns(databaseStandIn);
    }

    /// <summary>Declares a container and its partition key paths, for example <c>/BranchID</c> and <c>/ItemType</c>.</summary>
    public RecordingCosmos Container(string id, params string[] partitionKeyPaths)
    {
        gateway.Declare(id, partitionKeyPaths);

        var container = Substitute.ForPartsOf<RecordingContainer>();
        container.Configure(this, id, partitionKeyPaths, queryClient.GetDatabase(database).GetContainer(id));

        lock (containers)
            containers[id] = container;

        store.TryAdd(id, new ConcurrentDictionary<string, JObject>());
        return this;
    }

    /// <summary>The documents in a container now, ordered by id and partition key.</summary>
    public IReadOnlyList<JObject> Stored(string container) =>
        store[container].OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JObject)x.Value.DeepClone()).ToList();

    /// <summary>The documents in a container as one line each, ordered by id and partition key.</summary>
    public IReadOnlyList<string> StoredLines(string container) =>
        Stored(container).Select(x => x.ToString(Newtonsoft.Json.Formatting.None)).ToList();

    /// <summary>Puts a document into a container as if an earlier sync had written it. It is not recorded as a write.</summary>
    public RecordingCosmos Seed(string container, object document)
    {
        var body = JObject.FromObject(document);
        store[container][Key((string?)body["id"] ?? "", PartitionKeyOf(container, body).ToString())] = body;
        return this;
    }

    /// <summary>
    /// Sets the documents a query on <paramref name="container"/> returns. The answer gets the SQL text, so a test
    /// can return documents only for the query it expects.
    /// </summary>
    public RecordingCosmos Answer(string container, Func<string, IEnumerable<object>> answer)
    {
        gateway.Answer(container, query => answer(query).Select(JObject.FromObject));
        return this;
    }

    /// <summary>The writes as text lines, the form most assertions compare.</summary>
    public IReadOnlyList<string> WriteLines => Writes.Select(x => x.ToString()).ToList();

    public void Dispose() => queryClient.Dispose();

    private static string Key(string id, string partitionKey) => id + "|" + partitionKey;

    // The partition key a document lives under: the values at the container's paths, built the way the SDK builds it.
    private PartitionKey PartitionKeyOf(string container, JObject document)
    {
        RecordingContainer standIn;
        lock (containers)
            standIn = containers[container];

        var builder = new PartitionKeyBuilder();
        foreach (var path in standIn.PartitionKeyPaths)
        {
            var value = document.SelectToken(path.TrimStart('/').Replace('/', '.'));
            switch (value?.Type)
            {
                case null or JTokenType.Null: builder.AddNullValue(); break;
                case JTokenType.Boolean: builder.Add((bool)value); break;
                case JTokenType.Integer or JTokenType.Float: builder.Add((double)value); break;
                default: builder.Add((string)value!); break;
            }
        }
        return builder.Build();
    }

    private void Apply(Write write)
    {
        var documents = store[write.Container];

        switch (write.Kind)
        {
            case "upsert":
                var body = (JObject)write.Body!.DeepClone();
                documents[Key(write.Id, PartitionKeyOf(write.Container, body).ToString())] = body;
                break;

            case "patch":
                if (documents.TryGetValue(Key(write.Id, write.PartitionKey!), out var stored))
                    foreach (var operation in (JArray)write.Body!)
                    {
                        var path = ((string)operation["path"]!).TrimStart('/').Split('/');
                        var parent = path[..^1].Aggregate((JToken)stored, (x, name) => x[name]!);
                        parent[path[^1]] = operation["value"]?.DeepClone();
                    }
                break;

            case "delete":
                documents.TryRemove(Key(write.Id, write.PartitionKey!), out _);
                break;
        }
    }

    // Answers a query from the stored documents. It understands the filters reference updates use: equalities on
    // members, joined with AND. Anything else throws, so a test learns that it needs an Answer of its own.
    private IEnumerable<JObject> QueryStore(string container, string query)
    {
        const string prefix = "SELECT VALUE root FROM root WHERE ";
        if (!query.StartsWith(prefix, StringComparison.Ordinal))
            throw new NotSupportedException($"The Cosmos stand-in cannot answer this query: {query}");

        var where = query[prefix.Length..];
        var terms = System.Text.RegularExpressions.Regex.Matches(where,
            """\(root((?:\["[^"]+"\])+) = ("(?:[^"\\]|\\.)*"|-?\d+(?:\.\d+)?|true|false|null)\)""");

        var rest = System.Text.RegularExpressions.Regex.Replace(where,
            """\(root((?:\["[^"]+"\])+) = ("(?:[^"\\]|\\.)*"|-?\d+(?:\.\d+)?|true|false|null)\)""", "");
        if (rest.Replace("AND", "").Trim('(', ')', ' ').Length > 0)
            throw new NotSupportedException($"The Cosmos stand-in cannot answer this query: {query}");

        var filters = terms.Select(x => (
            Path: System.Text.RegularExpressions.Regex.Matches(x.Groups[1].Value, "\"([^\"]+)\"").Select(y => y.Groups[1].Value).ToArray(),
            Value: JToken.Parse(x.Groups[2].Value))).ToList();

        return Stored(container).Where(document => filters.All(filter =>
        {
            JToken? value = document;
            foreach (var name in filter.Path)
                value = value is JObject member ? member[name] : null;

            return JToken.DeepEquals(value ?? JValue.CreateNull(), filter.Value) ||
                (value?.Type is JTokenType.Integer or JTokenType.Float && filter.Value.Type is JTokenType.Integer or JTokenType.Float &&
                    (double)value == (double)filter.Value);
        }));
    }

    private Task<ItemResponse<T>> Record<T>(Write write, T? resource)
    {
        writes.Enqueue(write);

        if (write.PartitionKey is not null && WrongShape(write) is { } refused)
            return Task.FromException<ItemResponse<T>>(refused);

        if (Refuse(write))
            return Task.FromException<ItemResponse<T>>(
                new CosmosException("The test refused this write.", HttpStatusCode.ServiceUnavailable, 0, "activity", 0));

        Apply(write);

        var response = Substitute.For<ItemResponse<T>>();
        response.StatusCode.Returns(write.Kind == "delete" ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        response.Resource.Returns(resource);
        return Task.FromResult(response);
    }

    // A key with another number of components than the container's partition key is refused, as Cosmos DB refuses
    // it: the SDK throws for a key with more components than the container's definition, and the service answers 400
    // with substatus 1001 for one that does not match it otherwise.
    private Exception? WrongShape(Write write)
    {
        RecordingContainer container;
        lock (containers)
            container = containers[write.Container];

        var components = JArray.Parse(write.PartitionKey!).Count;
        var defined = container.PartitionKeyPaths.Length;
        if (components == defined)
            return null;

        return components > defined
            ? new ArgumentException("PartitionKey has more components than defined the collection resource.")
            : new CosmosException($"The partition key {write.PartitionKey} does not match the container's definition " +
                $"({string.Join(", ", container.PartitionKeyPaths)}).", HttpStatusCode.BadRequest, 1001, "activity", 0);
    }

    /// <summary>
    /// The container stand-in. Only the members the replication code calls are written here. NSubstitute supplies
    /// the rest, which the replication code never calls.
    /// </summary>
    public abstract class RecordingContainer : Microsoft.Azure.Cosmos.Container
    {
        private RecordingCosmos cosmos = null!;
        private string id = "";
        private string[] partitionKeyPaths = [];
        private Microsoft.Azure.Cosmos.Container queries = null!;

        internal void Configure(RecordingCosmos cosmos, string id, string[] partitionKeyPaths, Microsoft.Azure.Cosmos.Container queries)
        {
            this.cosmos = cosmos;
            this.id = id;
            this.partitionKeyPaths = partitionKeyPaths;
            this.queries = queries;
        }

        internal string[] PartitionKeyPaths => partitionKeyPaths;

        public override string Id => id;

        public override Task<ContainerResponse> ReadContainerAsync(ContainerRequestOptions? requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            var properties = partitionKeyPaths.Length == 1
                ? new ContainerProperties(id, partitionKeyPaths[0])
                : new ContainerProperties(id, partitionKeyPaths.ToList());

            var response = Substitute.For<ContainerResponse>();
            response.Resource.Returns(properties);
            return Task.FromResult(response);
        }

        public override Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey? partitionKey = null,
            ItemRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            var body = JObject.FromObject(item!);
            return cosmos.Record(new Write("upsert", id, (string?)body["id"] ?? "", partitionKey?.ToString(), body), item);
        }

        public override Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey,
            IReadOnlyList<PatchOperation> patchOperations, PatchItemRequestOptions? requestOptions = null,
            CancellationToken cancellationToken = default)
        {
            var operations = new JArray(patchOperations.Select(x => new JObject
            {
                ["op"] = x.OperationType.ToString(),
                ["path"] = x.Path,
                ["value"] = x.GetType().GetProperty("Value")?.GetValue(x) is { } value ? JToken.FromObject(value) : null,
            }));

            return cosmos.Record<T>(new Write("patch", this.id, id, partitionKey.ToString(), operations), default);
        }

        public override Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey,
            ItemRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            cosmos.Record<T>(new Write("delete", this.id, id, partitionKey.ToString(), null), default);

        public override IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false,
            string? continuationToken = null, QueryRequestOptions? requestOptions = null,
            CosmosLinqSerializerOptions? linqSerializerOptions = null) =>
            queries.GetItemLinqQueryable<T>(allowSynchronousQueryExecution, continuationToken, requestOptions, linqSerializerOptions);
    }

    /// <summary>
    /// Answers the gateway calls that a query makes: the account, the container's properties, its partition key
    /// ranges (one range for the whole key space) and the query itself. Anything else answers 501, so a test fails
    /// loudly if the code under test starts to make other calls.
    /// </summary>
    private sealed class QueryGateway(Func<string, string, IEnumerable<JObject>> fallback) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, string[]> keys = new();
        private readonly ConcurrentDictionary<string, Func<string, IEnumerable<JObject>>> answers = new();
        private readonly ConcurrentDictionary<string, string> rids = new();

        public ConcurrentQueue<(string Container, string Query)> Queries { get; } = new();

        public void Declare(string container, string[] partitionKeyPaths)
        {
            keys[container] = partitionKeyPaths;
            // A collection's resource id is its database's four bytes followed by four bytes of its own.
            rids.GetOrAdd(container, _ => Convert.ToBase64String([0xA9, 0x9E, 0x02, 0x00, 0xA6, 0x42, 0x06, (byte)(0x10 + rids.Count)])
                .Replace('/', '-'));
        }

        public void Answer(string container, Func<string, IEnumerable<JObject>> answer) => answers[container] = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            var parts = path.Split('/');

            if (path.Length == 0)
                return Json(Account());

            // dbs/{database}/colls/{container}: the container's properties.
            if (parts is ["dbs", _, "colls", var container] && request.Method == HttpMethod.Get)
            {
                var rid = rids[container];
                return Json(new JObject
                {
                    ["id"] = container,
                    ["partitionKey"] = new JObject
                    {
                        ["paths"] = new JArray(keys[container]),
                        ["kind"] = keys[container].Length > 1 ? "MultiHash" : "Hash",
                        ["version"] = 2,
                    },
                    ["indexingPolicy"] = new JObject { ["indexingMode"] = "consistent", ["automatic"] = true },
                    ["_rid"] = rid,
                    ["_self"] = $"dbs/{rid[..8]}/colls/{rid}/",
                    ["_etag"] = "\"0\"",
                    ["_ts"] = 1,
                });
            }

            // dbs/{databaseRid}/colls/{containerRid}/pkranges: one range, then "no change" when asked again.
            if (parts is ["dbs", _, "colls", var containerRid, "pkranges"])
            {
                if (request.Headers.Contains("If-None-Match"))
                    return new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new StringContent("") };

                var response = Json(new JObject
                {
                    ["_rid"] = containerRid,
                    ["PartitionKeyRanges"] = new JArray(new JObject
                    {
                        ["_rid"] = containerRid + "AAAAAAAAUA==",
                        ["id"] = "0",
                        ["_etag"] = "\"1\"",
                        ["minInclusive"] = "",
                        ["maxExclusive"] = "FF",
                        ["ridPrefix"] = 0,
                        ["throughputFraction"] = 1,
                        ["status"] = "online",
                        ["parents"] = new JArray(),
                        ["_ts"] = 1,
                        ["lsn"] = 1,
                    }),
                    ["_count"] = 1,
                });
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"1\"");
                return response;
            }

            // dbs/{database}/colls/{container}/docs with the query header: recorded, then answered.
            if (parts is ["dbs", _, "colls", var queried, "docs"] && request.Headers.Contains("x-ms-documentdb-isquery"))
            {
                var query = (string)JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))["query"]!;
                Queries.Enqueue((queried, query));

                var documents = answers.TryGetValue(queried, out var answer) ? answer(query).ToList() : fallback(queried, query).ToList();
                return Json(new JObject { ["_rid"] = rids[queried], ["Documents"] = new JArray(documents), ["_count"] = documents.Count });
            }

            return new HttpResponseMessage(HttpStatusCode.NotImplemented)
            {
                Content = new StringContent($"The query gateway stand-in does not answer {request.Method} {path}."),
            };
        }

        private static JObject Account() => new()
        {
            ["_self"] = "",
            ["id"] = "localhost",
            ["_rid"] = "localhost",
            ["media"] = "//media/",
            ["addresses"] = "//addresses/",
            ["_dbs"] = "//dbs/",
            ["writableLocations"] = new JArray(new JObject { ["name"] = "Local", ["databaseAccountEndpoint"] = "https://localhost:8081/" }),
            ["readableLocations"] = new JArray(new JObject { ["name"] = "Local", ["databaseAccountEndpoint"] = "https://localhost:8081/" }),
            ["enableMultipleWriteLocations"] = false,
            ["userReplicationPolicy"] = new JObject { ["asyncReplication"] = false, ["minReplicaSetSize"] = 1, ["maxReplicasetSize"] = 4 },
            ["userConsistencyPolicy"] = new JObject { ["defaultConsistencyLevel"] = "Session" },
            ["systemReplicationPolicy"] = new JObject { ["minReplicaSetSize"] = 1, ["maxReplicasetSize"] = 4 },
            ["readPolicy"] = new JObject { ["primaryReadCoefficient"] = 1, ["secondaryReadCoefficient"] = 1 },
            // What the local emulator reports. The SDK plans queries locally with these limits.
            ["queryEngineConfiguration"] =
                "{\"allowNewKeywords\":true,\"maxJoinsPerSqlQuery\":10,\"maxQueryRequestTimeoutFraction\":0.9," +
                "\"maxSqlQueryInputLength\":524288,\"maxUdfRefPerSqlQuery\":10,\"queryMaxInMemorySortDocumentCount\":-1000," +
                "\"spatialMaxGeometryPointCount\":256,\"sqlAllowNonFiniteNumbers\":false,\"sqlDisableOptimizationFlags\":0," +
                "\"enableSpatialIndexing\":true,\"maxInExpressionItemsCount\":2147483647,\"maxLogicalAndPerSqlQuery\":2147483647," +
                "\"maxLogicalOrPerSqlQuery\":2147483647,\"maxSpatialQueryCells\":2147483647,\"sqlAllowAggregateFunctions\":true," +
                "\"sqlAllowGroupByClause\":true,\"sqlAllowLike\":true,\"sqlAllowSubQuery\":true,\"sqlAllowScalarSubQuery\":true," +
                "\"sqlAllowTop\":true}",
        };

        private static HttpResponseMessage Json(JObject body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("x-ms-request-charge", "1");
            response.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
            response.Headers.Add("x-ms-session-token", "0:-1#1");
            return response;
        }
    }
}
