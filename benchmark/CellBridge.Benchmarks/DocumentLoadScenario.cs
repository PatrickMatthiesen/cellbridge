using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using NBomber.Contracts;
using NBomber.CSharp;
using Npgsql;

internal sealed partial class DocumentLoadScenario(string backend, LoadResources resources) : IAsyncDisposable
{
    private readonly ConcurrentBag<Session> sessions = [];
    private readonly CellBridgeActor actor = new(new SubjectIdentity("local:integration-writer", "integration-writer", "Integration writer"), CanCreate: true);
    private readonly Dictionary<string, string> attributes = [];
    private DocumentLoadSettings settings = new();
    private StorageProvider provider = null!;
    private CellBridgeDocumentService service = null!;
    private NpgsqlDataSource? source;
    private byte[] payload = [];
    private string? scratch;
    private bool initialized;
    private Session[] httpSessions = [];
    private readonly List<HttpDocument> httpDocuments = [];
    private long commits;
    private long conflicts;
    private long replays;
    private long replayConflicts;
    internal const string ConflictStatus = "document_conflict";
    internal const string ReplayConflictStatus = "document_replay_conflict";

    public ScenarioProps Create() => CellBridgeLoadTests.Defaults(Scenario.Create("document-" + backend, async context =>
    {
        resources.Observe(context);
        try
        {
            if (backend == "http") return settings.UserModel ? await RunUsersAsync(context) : await RunHttpAsync(context);
            if (!context.ScenarioInstanceData.TryGetValue("document-session", out var stored))
            {
                stored = new Session();
                context.ScenarioInstanceData["document-session"] = stored;
                sessions.Add((Session)stored);
            }
            var session = (Session)stored;
            var mode = settings.Modes[session.Iteration++ % settings.Modes.Length];
            var entry = await CellBridgeLoadTests.StepAsync("prepare-" + mode, context, () => PrepareAsync(session, mode, context));
            var before = entry.State;
            var result = await CellBridgeLoadTests.StepAsync("save-" + mode, context, () => SaveAsync(session, entry, entry.Request!, resources.CancellationToken));
            entry.State = result;
            entry.Saves++;
            if (entry.State.ContentVersion != before.ContentVersion + 1)
                throw new InvalidDataException("A new save did not advance the content version.");
            if (settings.Retry)
            {
                var retry = await CellBridgeLoadTests.StepAsync("retry-" + mode, context,
                    () => SaveAsync(session, entry, entry.Request!, resources.CancellationToken));
                if (retry.ContentVersion != entry.State.ContentVersion || retry.Content != entry.State.Content ||
                    retry.Receipts.Length != entry.State.Receipts.Length)
                    throw new InvalidDataException("An identical retry changed the saved document.");
            }
            if (entry.Saves % settings.QueryEvery == 0)
                await CellBridgeLoadTests.StepAsync("query-and-retention-" + mode, context, () => DiagnoseAsync(session, entry, resources.CancellationToken));
            return Response.Ok();
        }
        catch (DocumentConflictException error)
        {
            return Response.Fail(statusCode: error.StatusCode);
        }
        catch (RequestFailureException error)
        {
            return Response.Fail(statusCode: error.StatusCode);
        }
        catch (Exception error)
        {
            if (backend != "http" || error is InvalidDataException or ArgumentException ||
                resources.CancellationToken.IsCancellationRequested)
                resources.Stop("Document workflow failed: " +
                    (backend == "http" && error is InvalidDataException ? error.Message : error.GetType().Name));
            return Response.Fail(statusCode: error.GetType().Name);
        }
    })).WithMaxFailCount(int.MaxValue).WithThresholds().WithInit(context => resources.InitializeAsync(async () =>
    {
        settings = context.CustomSettings.Get<DocumentLoadSettings>() ?? new();
        settings.Validate();
        var documentLimit = Math.Max(10_000, checked(settings.Clients * settings.DocumentsPerClient));
        if (backend == "http" && settings.UserModel)
            resources.ConfigureDocumentUsers(settings.Clients, true, documentLimit);
        payload = settings.UserModel ? [] : StorageBenchmarkInputs.Package(settings.SizeMiB, settings.Seed);
        var limits = new StorageLimits { MaxDocuments = documentLimit };
        if (backend == "memory") provider = new(new InMemoryStateStore(), new InMemoryContentStore(), limits);
        else
        {
            var environment = await resources.EnvironmentAsync();
            if (backend == "http" && environment.Backend == "sqlserver")
            {
                var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>()
                    .UseSqlServer(environment.DocumentConnectionString).Options;
                provider = new(new SqlServerStateStore(options, limits),
                    new SqlServerContentStore(options, limits.MaxObjectBytes), limits);
            }
            else
            {
                source = NpgsqlDataSource.Create(backend == "filesystem" ? environment.FileSystemConnectionString : environment.PostgreSqlDocumentConnectionString);
                if (backend == "filesystem")
                {
                    scratch = Path.Combine(Path.GetTempPath(), "cellbridge-load-" + Guid.NewGuid().ToString("N"));
                    provider = new(new PostgreSqlStateStore(source, limits),
                        new PostgreSqlFileSystemContentStore(source, scratch, limits.MaxObjectBytes), limits);
                }
                else provider = new(new PostgreSqlStateStore(source, limits), new PostgreSqlContentStore(source, limits.MaxObjectBytes), limits);
            }
        }
        await provider.State.CheckHealthAsync();
        service = new(provider);
        if (backend == "http")
        {
            if (settings.UserModel) await InitializeUsersAsync();
            else await InitializeHttpAsync();
        }
        initialized = true;
    }));

    private async Task InitializeHttpAsync()
    {
        var environment = await resources.EnvironmentAsync();
        var documentCount = settings.Workload == "contention" ? 1 : checked(settings.Clients * settings.DocumentsPerClient);
        for (var i = 0; i < documentCount; i++)
        {
            var state = await service.CreateAsync("/shared/load-" + Guid.NewGuid().ToString("N") + ".docx", payload, actor,
                resources.CancellationToken) ?? throw new InvalidDataException("HTTP seed document was not created.");
            var restored = await StoredDocument.RestoreAsync(state, provider.Content, cancellationToken: resources.CancellationToken);
            httpDocuments.Add(new(state, restored.FilePartition.ProtocolIdentity.CellId));
        }
        httpSessions = new Session[settings.Clients];
        for (var client = 0; client < httpSessions.Length; client++)
        {
            var session = new Session { Random = new(unchecked(settings.Seed + client * 397)) };
            sessions.Add(session);
            session.Http = await environment.CreateClientAsync(resources.CancellationToken);
            session.Transport = new(session.Http, new Uri(environment.HttpEndpoint, "/_vti_bin/cellstorage.svc"));
            session.Views = (settings.Workload == "contention" ? httpDocuments.Take(1) :
                httpDocuments.Skip(client * settings.DocumentsPerClient).Take(settings.DocumentsPerClient))
                .Select(document => new HttpView(document)).ToArray();
            session.Payloads = Enumerable.Range(0, settings.PayloadVariants).Select(variant =>
                StorageBenchmarkInputs.Package(settings.SizeMiB,
                    unchecked(settings.Seed + 1 + client * settings.PayloadVariants + variant))).ToArray();
            session.PayloadHashes = session.Payloads.Select(bytes => Convert.ToHexString(SHA256.HashData(bytes))).ToArray();
            foreach (var view in session.Views)
            {
                // Authentication, the initial graph and download are outside NBomber's timed load.
                var response = await QueryHttpAsync(session, view, resources.CancellationToken);
                var graph = PartitionGraphSnapshot.Create(response.DataElementPackage!.DataElements, view.Index);
                if (!graph.Materialize().AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException("The HTTP seed query did not return the seeded content.");
                var downloaded = await session.Http.GetByteArrayAsync(view.Document.Initial.Path, resources.CancellationToken);
                if (!downloaded.AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException("The HTTP seed download did not return the seeded content.");
            }
            httpSessions[client] = session;
        }
        Console.WriteLine($"Document HTTP workload: sessions={settings.Clients}, authenticated-users={environment.UserCount}, " +
            $"documents={documentCount}, writes={settings.WritePercent}%.");
    }

    private async Task<IResponse> RunHttpAsync(IScenarioContext context)
    {
        var instance = context.ScenarioInfo.InstanceNumber;
        if (instance < 0 || instance >= httpSessions.Length)
            throw new ArgumentException("Document Clients must cover the scenario's instance numbers.");
        var session = httpSessions[instance];
        var view = session.Views[session.Iteration++ % session.Views.Length];
        var cancellationToken = resources.CancellationToken;
        if (session.Random.Next(100) >= settings.WritePercent)
        {
            if (session.Iteration % 2 == 0)
                await HttpStepAsync("query", context, () => QueryHttpAsync(session, view, cancellationToken));
            else
            {
                var bytes = await HttpStepAsync("download", context,
                    () => session.Http!.GetByteArrayAsync(view.Document.Initial.Path, cancellationToken));
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (bytes.Length != payload.Length ||
                    !hash.Equals(view.Document.Initial.Content.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    !httpSessions.SelectMany(s => s.PayloadHashes).Contains(hash))
                    throw new InvalidDataException("HTTP download is not a seeded or generated document payload.");
            }
            return Response.Ok();
        }

        await HttpStepAsync("query", context, () => QueryHttpAsync(session, view, cancellationToken));
        var variant = (view.LastCommittedVariant + 1) % session.Payloads.Length;
        var request = CreateHttpSave(view, session.Payloads[variant], session.Iteration, session.Random);
        // Build the graph and binary outside the measured HTTP request step. Payloads were generated during init.
        var binary = request.ToByteArray();
        var proposed = ((PutChangesSubRequestData)request.SubRequests.Single().Data!).StorageIndex;
        await SaveHttpStepAsync("save", context, session, view, request, binary, false, cancellationToken);
        if (!view.Document.Committed.TryAdd(proposed, session.PayloadHashes[variant]))
            throw new InvalidDataException("A fresh HTTP save reused an accepted storage index.");
        Interlocked.Increment(ref commits);
        view.LastCommittedVariant = variant;
        if (settings.Retry)
        {
            await SaveHttpStepAsync("replay", context, session, view, request, binary, true, cancellationToken);
            Interlocked.Increment(ref replays);
        }
        return Response.Ok();
    }

    private static FsshttpbCellRequest CreateHttpSave(HttpView view, byte[] content, int iteration, Random random)
    {
        var idBytes = new byte[16];
        random.NextBytes(idBytes);
        var id = new Guid(idBytes);
        var identity = new StorageManifestBuilder.StableIdentity(new(1, id), new(2, id), new(3, id),
            new(4, id), new(5, id), new(6, id), new(7, id), view.Document.Cell, id);
        var generated = FileContentPartitionBuilder.BuildQueryChangesResponse(1, content, identity, (ulong)iteration + 1);
        var request = new FsshttpbCellRequest { DataElementPackage = generated.DataElementPackage };
        request.SubRequests.Add(new(RequestTypes.PutChanges)
        {
            RequestId = (ulong)iteration + 1,
            Data = new PutChangesSubRequestData
            {
                StorageIndex = identity.ObjectDataBlobGuid,
                ExpectedStorageIndex = view.Index,
                HasAdditionalFlags = true,
                AdditionalFlagsBits = 1,
            },
        });
        return request;
    }

    private static async Task<FsshttpbResponse> QueryHttpAsync(Session session, HttpView view, CancellationToken cancellationToken)
    {
        var query = new FsshttpbCellRequest();
        query.SubRequests.Add(new(RequestTypes.QueryChanges)
        {
            RequestId = 1,
            Data = new QueryChangesSubRequestData
            {
                IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = view.Knowledge,
            },
        });
        var response = await SendHttpAsync(session, view.Document.Initial.Path, query, cancellationToken);
        CheckHttpResponse(response);
        if (response.SubResponses.Single().Data is not QueryChangesSubResponseData data || data.PartialResult ||
            data.StorageIndexExtendedGuid.IsNull || data.KnowledgeBytes is null)
            throw new InvalidDataException("HTTP query did not return complete storage index and knowledge.");
        view.Index = data.StorageIndexExtendedGuid;
        view.Knowledge = ClientKnowledge.Deserialize(new(data.KnowledgeBytes));
        return response;
    }

    private async Task SaveHttpStepAsync(string name, IScenarioContext context, Session session, HttpView view,
        FsshttpbCellRequest request, byte[] binary, bool replay, CancellationToken cancellationToken)
    {
        await HttpStepAsync(name, context, async () =>
        {
            var wire = await SendHttpBinaryAsync(session, view.Document.Initial.Path, request, binary, cancellationToken);
            if (settings.Workload == "contention" && !wire.Status && wire.SubResponses.Count == 1 && wire.SubResponses[0] is
                { Status: true, RequestType: RequestTypes.PutChanges, Error: { Type: ErrorType.Cell,
                    ErrorCode: (ulong)CellErrorCode.CoherencyFailure } })
            {
                if (replay) Interlocked.Increment(ref replayConflicts);
                else Interlocked.Increment(ref conflicts);
                throw new DocumentConflictException(replay ? ReplayConflictStatus : ConflictStatus);
            }
            CheckHttpResponse(wire);
            if (wire.SubResponses.Single().Data is not PutChangesSubResponseData { PutChangesResponse: { } put } data ||
                !put.AppliedStorageIndexID.Equals(((PutChangesSubRequestData)request.SubRequests.Single().Data!).StorageIndex) ||
                !replay && put.DataElementAdded.Count == 0)
                throw new InvalidDataException("HTTP save did not acknowledge the proposed graph with its requested applied index and added elements.");
            if (data.KnowledgeBytes is null)
                throw new InvalidDataException("HTTP save did not return server knowledge.");
            view.Index = put.AppliedStorageIndexID;
            view.Knowledge = ClientKnowledge.Deserialize(new(data.KnowledgeBytes));
            return true;
        }, replay ? "document_replay" : "document_commit");
    }

    private async Task<T> HttpStepAsync<T>(string name, IScenarioContext context, Func<Task<T>> action,
        string successCode = "200")
    {
        Exception? fatal = null;
        var response = await Step.Run(name, context, async () =>
        {
            try { return Response.Ok(await action(), statusCode: successCode); }
            catch (DocumentConflictException error) { return Response.Fail<T>(statusCode: error.StatusCode); }
            catch (RequestFailureException error) { return Response.Fail<T>(statusCode: error.StatusCode); }
            catch (HttpRequestException error)
            {
                return Response.Fail<T>(statusCode: error.StatusCode is { } code ? ((int)code).ToString() : "http_transport_error");
            }
            catch (OperationCanceledException error)
            {
                if (resources.CancellationToken.IsCancellationRequested) fatal = error;
                return Response.Fail<T>(statusCode: fatal is null ? "request_timeout" : "cancelled");
            }
            catch (Exception error)
            {
                if (error is InvalidDataException or ArgumentException) fatal = error;
                return Response.Fail<T>(statusCode: error.GetType().Name);
            }
        });
        if (fatal is not null) throw fatal;
        if (response.IsError)
        {
            if (response.StatusCode is ConflictStatus or ReplayConflictStatus)
                throw new DocumentConflictException(response.StatusCode);
            throw new RequestFailureException(response.StatusCode);
        }
        return response.Payload.Value!;
    }

    private async Task<Entry> PrepareAsync(Session session, string mode, IScenarioContext context)
    {
        if (!session.Entries.TryGetValue(mode, out var entry) || mode == "captured" && entry.Saves == 2)
        {
            if (entry is not null) await VerifyEntryAsync(entry);
            var state = await service.CreateAsync("/shared/load-" + Guid.NewGuid().ToString("N") + ".docx", payload, actor,
                resources.CancellationToken) ?? throw new InvalidDataException("Document was not created.");
            entry = new(state, payload);
            session.Entries[mode] = entry;
            session.AllEntries.Add(entry);
        }
        entry.State = await provider.State.FindByResourceIdAsync(entry.State.ResourceId, resources.CancellationToken)
            ?? throw new InvalidDataException("Document disappeared.");
        var restored = await StoredDocument.RestoreAsync(entry.State, provider.Content, cancellationToken: resources.CancellationToken);
        entry.Expected = mode == "changed-full" ? StorageBenchmarkInputs.Package(settings.SizeMiB, settings.Seed + entry.Saves + 1) : payload;
        entry.Request = mode == "captured"
            ? SyntheticWorkload.Capture(Path.Combine(CellBridgeLoadTests.RepositoryRoot(), "testdata/sharepoint/fixtures/save_reopen"), entry.Saves == 0 ? "save-first" : "save-second")
            : SyntheticWorkload.Save(restored, entry.Expected, mode, entry.Saves, context.Random);
        if (mode == "captured" && entry.Saves == 0)
            ((PutChangesSubRequestData)entry.Request.SubRequests.Single().Data!).ExpectedStorageIndex = restored.FilePartition.FileGraph.StorageIndex;
        entry.Captured = mode == "captured";
        return entry;
    }

    private async Task<DocumentState> SaveAsync(Session session, Entry entry, FsshttpbCellRequest request, CancellationToken cancellationToken)
    {
        var result = await service.ExecuteAsync(entry.State.ResourceId, DocumentPartitionKind.FileContents, request, attributes, actor, cancellationToken);
        if (result.IsCancelled) throw new OperationCanceledException(cancellationToken);
        CheckResponse(result.Response);
        return result.State;
    }

    private static async Task<FsshttpbResponse> SendHttpAsync(Session session, string path, FsshttpbCellRequest request, CancellationToken cancellationToken)
    {
        return await SendHttpBinaryAsync(session, path, request, request.ToByteArray(), cancellationToken);
    }

    private static async Task<FsshttpbResponse> SendHttpBinaryAsync(Session session, string path, FsshttpbCellRequest request,
        byte[] binary, CancellationToken cancellationToken)
    {
        try
        {
            var wire = await session.Transport!.SendCellAsync(new Uri(session.Http!.BaseAddress!, path).AbsoluteUri,
                binary, null, true, request.UserAgentGuid, cancellationToken);
            if ((int)wire.StatusCode != 200)
                throw new HttpRequestException("The Cell HTTP request failed.", null, wire.StatusCode);
            var soap = XDocument.Parse(wire.Soap);
            if (soap.Descendants().Any(e => e.Name.LocalName == "Fault"))
                throw new RequestFailureException("soap_Fault");
            var soapErrors = soap.Descendants().Attributes().Where(a =>
                a.Name.LocalName is "ErrorCode" or "VersionErrorCode" && a.Value != "Success").ToArray();
            if (soapErrors.Length != 0)
                throw new RequestFailureException("soap_" + SafeProtocolCode(soapErrors.Last().Value));
            if (wire.Binary is null) throw new InvalidDataException("SOAP response has no binary result.");
            var reader = new BinaryReaderEx(wire.Binary);
            var response = FsshttpbResponse.Deserialize(reader);
            if (reader.Remaining != 0) throw new InvalidDataException("Binary response has unparsed bytes.");
            return response;
        }
        catch (Exception error) when (error is XmlException or EndOfStreamException or FormatException)
        {
            throw new InvalidDataException("The Cell HTTP response contains malformed XML or binary data.", error);
        }
    }

    private static void CheckHttpResponse(FsshttpbResponse response)
    {
        if (response.Status)
        {
            if (response.Error is null)
                throw new InvalidDataException("The failed Cell response has no protocol error.");
            throw new RequestFailureException($"cell_{response.Error.Type}_{response.Error.ErrorCode}");
        }
        if (response.SubResponses.Count != 1)
            throw new InvalidDataException($"Cell response returned {response.SubResponses.Count} operations; expected one.");
        var operation = response.SubResponses.Single();
        if (operation.Status)
        {
            if (operation.Error is null)
                throw new InvalidDataException("The failed Cell operation has no protocol error.");
            throw new RequestFailureException($"cell_{operation.Error.Type}_{operation.Error.ErrorCode}");
        }
    }

    private static void CheckResponse(FsshttpbResponse response)
    {
        if (response.Status)
            throw new InvalidDataException("Cell response failed: " + DescribeProtocolError(response.Error) + ".");
        if (response.SubResponses.Count != 1)
            throw new InvalidDataException($"Cell response returned {response.SubResponses.Count} operations; expected one.");
        var operation = response.SubResponses.Single();
        if (operation.Status)
            throw new InvalidDataException($"Cell {operation.RequestType} failed: {DescribeProtocolError(operation.Error)}.");
    }

    private static string DescribeProtocolError(ResponseError? error)
    {
        if (error is null) return "missing protocol error";
        var name = error.Type switch
        {
            ErrorType.Cell => Enum.GetName((CellErrorCode)error.ErrorCode),
            ErrorType.Protocol => Enum.GetName((ProtocolErrorCode)error.ErrorCode),
            _ => null,
        };
        return $"{error.Type} code {error.ErrorCode}" + (name is null ? "" : $" ({name})");
    }

    private static string SafeProtocolCode(string code) => code.Length is > 0 and <= 64 &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : "unrecognized-code";

    private async Task<bool> DiagnoseAsync(Session session, Entry entry, CancellationToken cancellationToken)
    {
        var restored = await StoredDocument.RestoreAsync(entry.State, provider.Content, cancellationToken: cancellationToken);
        var graph = restored.FilePartition.FileGraph;
        if (!graph.AnalyzeRetention().Complete) throw new InvalidDataException("Saved graph has unresolved retention requirements.");
        var current = ClientKnowledge.Deserialize(new(BinaryKnowledgeBuilder.FromElements(graph.ElementMetadata,
            restored.FilePartition.ProtocolIdentity.CellId.ShortId, restored.FilePartition.KnowledgeSequence,
            mappingSerials: graph.MappingSerials.Values.SelectMany(s => s))));
        foreach (var knowledge in new[] { null, entry.PriorKnowledge, current })
        {
            var query = new FsshttpbCellRequest();
            query.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData
                { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge } });
            var result = await service.ExecuteAsync(entry.State.ResourceId, DocumentPartitionKind.FileContents, query, attributes, actor, cancellationToken);
            if (result.IsCancelled) throw new OperationCanceledException(cancellationToken);
            CheckResponse(result.Response);
        }
        entry.PriorKnowledge = current;
        await VerifyEntryAsync(entry);
        return true;
    }

    private async Task VerifyEntryAsync(Entry entry)
    {
        var state = await provider.State.FindByResourceIdAsync(entry.State.ResourceId) ?? throw new InvalidDataException("Final document is missing.");
        var restored = await StoredDocument.RestoreAsync(state, provider.Content);
        var bytes = restored.FilePartition.FileGraph.Materialize();
        if (!restored.FilePartition.FileGraph.AnalyzeRetention().Complete ||
            state.ContentVersion != entry.State.ContentVersion || state.Content != entry.State.Content ||
            state.Receipts.Length != entry.Saves || !bytes.AsSpan().SequenceEqual(restored.Content) ||
            !entry.Captured && !bytes.AsSpan().SequenceEqual(entry.Expected))
            throw new InvalidDataException("Final document, receipts or graph differ from the saved result.");
    }

    public async Task VerifyAsync()
    {
        if (!initialized) return;
        if (backend == "http")
        {
            if (settings.UserModel) await CaptureUserDatabaseStatisticsAsync();
            if (settings.UserModel && userSessions.Count(user => user.Started) != settings.Clients)
                throw new InvalidDataException("The executed active-user count differs from the configured client count.");
            if (settings.UserModel && httpDocuments.Count != 0)
            {
                // All user documents started from the same immutable corpus.
                // Reverify those shared bytes once, then compare untouched states
                // exactly rather than downloading the same corpus for every clone.
                _ = await provider.Content.ReadManyVerifiedAsync(StorageReferences.Handles(httpDocuments[0].Initial),
                    provider.Limits.MaxObjectBytes);
            }
            foreach (var document in httpDocuments)
            {
                var state = await provider.State.FindByResourceIdAsync(document.Initial.ResourceId)
                    ?? throw new InvalidDataException("Final HTTP document is missing.");
                if (settings.UserModel && document.Committed.IsEmpty)
                {
                    if (DocumentStateJson.Serialize(state) != DocumentStateJson.Serialize(document.Initial))
                        throw new InvalidDataException("An untouched user document changed during load.");
                    continue;
                }
                var restored = await StoredDocument.RestoreAsync(state, provider.Content);
                var bytes = restored.FilePartition.FileGraph.Materialize();
                var committed = document.Committed;
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var index = restored.FilePartition.FileGraph.StorageIndex;
                var expectedHash = committed.Count == 0 ? document.Initial.Content.Sha256 :
                    committed.TryGetValue(index, out var acceptedHash) ? acceptedHash :
                    throw new InvalidDataException("The final HTTP storage index was not acknowledged as a commit.");
                if (!restored.FilePartition.FileGraph.AnalyzeRetention().Complete ||
                    state.ContentVersion != document.Initial.ContentVersion + committed.Count ||
                    state.Receipts.Length != committed.Count ||
                    !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) ||
                    !bytes.AsSpan().SequenceEqual(restored.Content) ||
                    state.Revisions.Zip(state.Revisions.Skip(1)).Any(pair =>
                        pair.First.Content.Sha256 == pair.Second.Content.Sha256))
                    throw new InvalidDataException("HTTP commits, versions, receipts or materialized content disagree.");
            }
            Console.WriteLine($"Document HTTP validation totals including warm-up: commits={commits}, conflicts={conflicts}, replays={replays}, replay-conflicts={replayConflicts}.");
            var uniqueCommittedPayloads = httpDocuments.SelectMany(document => document.Committed.Values).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Console.WriteLine($"Document HTTP committed payload hashes including warm-up: unique={uniqueCommittedPayloads}, commits={commits}.");
            if (settings.UserModel) PrintUserMetrics();
        }
        else foreach (var entry in sessions.SelectMany(s => s.AllEntries)) await VerifyEntryAsync(entry);
    }

    public async ValueTask DisposeAsync()
    {
        await StopUserDatabaseSamplingAsync();
        foreach (var session in sessions) session.Http?.Dispose();
        if (source is not null) await source.DisposeAsync();
        if (scratch is not null && Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
    }

    private sealed class Session
    {
        public int Iteration;
        public Dictionary<string, Entry> Entries { get; } = [];
        public List<Entry> AllEntries { get; } = [];
        public HttpClient? Http;
        public CellStorageTransport? Transport;
        public HttpView[] Views = [];
        public byte[][] Payloads = [];
        public string[] PayloadHashes = [];
        public Random Random = new();
    }
    private sealed class Entry(DocumentState state, byte[] expected)
    {
        public DocumentState State = state;
        public byte[] Expected = expected;
        public int Saves;
        public bool Captured;
        public FsshttpbCellRequest? Request;
        public ClientKnowledge? PriorKnowledge;
    }

    private sealed class HttpDocument(DocumentState initial, CellId cell)
    {
        public DocumentState Initial { get; } = initial;
        public CellId Cell { get; } = cell;
        public ConcurrentDictionary<ExGuid, string> Committed { get; } = new();
    }

    private sealed class HttpView(HttpDocument document)
    {
        public HttpDocument Document { get; } = document;
        public ExGuid Index = ExGuid.Null;
        public ClientKnowledge? Knowledge;
        public int LastCommittedVariant = -1;
    }

    private sealed class RequestFailureException(string statusCode) : Exception
    {
        public string StatusCode { get; } = statusCode;
    }

    private sealed class DocumentConflictException(string statusCode) : Exception
    {
        public string StatusCode { get; } = statusCode;
    }
}

internal sealed class DocumentLoadSettings
{
    public int SizeMiB { get; init; } = 1;
    public int Seed { get; init; } = 42;
    public string[] Modes { get; set; } = [];
    public bool Retry { get; init; } = true;
    public int QueryEvery { get; init; } = 32;
    public string Workload { get; init; } = "independent";
    public int Clients { get; init; } = 1;
    public int DocumentsPerClient { get; init; } = 1;
    public int WritePercent { get; init; } = 75;
    public int PayloadVariants { get; init; } = 2;
    public bool UserModel { get; init; }
    public int UserWarmUpMs { get; init; } = 30_000;
    public int UserMeasurementMs { get; init; } = 60_000;
    public int PackageKiB { get; init; } = 256;
    public int ChangedPartKiB { get; init; } = 16;
    public int SaveIntervalMs { get; init; } = 30_000;
    public int QueryIntervalMs { get; init; } = 30_000;
    public int OpenIntervalMs { get; init; } = 120_000;
    public void Validate()
    {
        // Bind into an empty array so a configured mode list replaces the defaults.
        if (Modes.Length == 0) Modes = ["full", "changed-full", "delta", "delta-chain", "captured"];
        if (Clients <= 0 || DocumentsPerClient <= 0 || WritePercent is < 0 or > 100 || PayloadVariants < 2 ||
            Workload is not ("independent" or "contention") ||
            UserModel && (UserWarmUpMs < 0 || UserMeasurementMs <= 0 || Retry || Workload != "independent" || PackageKiB < 16 ||
                ChangedPartKiB <= 0 || ChangedPartKiB > PackageKiB - 8 ||
                SaveIntervalMs <= 0 || QueryIntervalMs <= 0 || OpenIntervalMs <= 0) ||
            SizeMiB < 0 || QueryEvery <= 0 || Modes.Length == 0 || Modes.Any(m => m is not ("full" or "changed-full" or "delta" or "delta-chain" or "captured")))
            throw new ArgumentException("Invalid document load settings.");
    }
}
