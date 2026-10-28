using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.TestInfrastructure;
using NBomber.Contracts;
using NBomber.CSharp;
using Npgsql;

internal sealed partial class DocumentLoadScenario
{
    private IncrementalOfficeWorkload userCorpus = null!;
    private UserSession[] userSessions = [];
    private int nextUserRevision = 1;
    private long userSaveAttempts;
    private long userSaveBytes;
    private long userChangedChunkBytes;
    private long userMinimumSaveBytes = long.MaxValue;
    private long userMaximumSaveBytes;
    private long userMissedCadences;
    private long userLateStartMilliseconds;
    private long userMaximumLateStartMilliseconds;
    private long userActions;
    private long userQueries;
    private long userOpens;
    private long userMeasurementStart;
    private long userMeasurementMilliseconds;
    private long userMeasuredCommits;
    private long userMeasuredSaveStarts;
    private long userMeasuredLateAcknowledgements;

    private async Task InitializeUsersAsync()
    {
        var environment = await resources.EnvironmentAsync();
        var cancellationToken = resources.CancellationToken;
        if (environment.Backend == "postgresql")
        {
            await using var connection = new NpgsqlConnection(environment.DocumentConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT current_setting('server_version'), current_setting('max_wal_size'), " +
                "current_setting('fsync'), current_setting('synchronous_commit'), current_setting('full_page_writes')", connection);
            await using var row = await command.ExecuteReaderAsync(cancellationToken);
            await row.ReadAsync(cancellationToken);
            Console.WriteLine($"Document user database: PostgreSQL={row.GetString(0)}, max-wal-size={row.GetString(1)}, " +
                $"fsync={row.GetString(2)}, synchronous-commit={row.GetString(3)}, full-page-writes={row.GetString(4)}.");
        }
        userCorpus = new(settings.PackageKiB, settings.Seed, settings.ChangedPartKiB);
        var templateService = new CellBridgeDocumentService(new StorageProvider(new InMemoryStateStore(), provider.Content, provider.Limits));
        var owner = new CellBridgeActor(environment.GetUserIdentity(0), CanCreate: true);
        var template = await templateService.CreateAsync("/shared/template.docx", userCorpus.CreateContent(), owner, cancellationToken)
            ?? throw new InvalidDataException("The user workload template could not be created.");
        var original = await StoredDocument.RestoreAsync(template, provider.Content, cancellationToken);
        var seed = userCorpus.CreateSave(original, 1);
        var seeded = await templateService.ExecuteAsync(template.ResourceId, DocumentPartitionKind.FileContents,
            seed.Request, attributes, owner, cancellationToken);
        CheckResponse(seeded.Response);
        if (seeded.IsCancelled) throw new OperationCanceledException(cancellationToken);
        template = seeded.State with { Receipts = [], Coordination = CoordinationState.Empty };
        var templateGraph = await DocumentPartition.RestoreFileGraphAsync(template, provider.Content, provider.Limits, cancellationToken);
        if (!templateGraph.AnalyzeRetention().Complete || !templateGraph.Materialize().AsSpan().SequenceEqual(seed.Content))
            throw new InvalidDataException("The user workload template does not reproduce its seeded package.");
        payload = seed.Content;
        var cell = templateGraph.FileCell;
        var partition = template.Partitions.Single(partition => partition.Kind == 0);
        var initialKnowledge = ClientKnowledge.Deserialize(new(BinaryKnowledgeBuilder.FromElements(templateGraph.ElementMetadata,
            cell.ShortId, partition.Knowledge, mappingSerials: templateGraph.MappingSerials.Values.SelectMany(serials => serials))));
        var documentCount = checked(settings.Clients * settings.DocumentsPerClient);
        var requested = new (string Path, SubjectIdentity Owner)[documentCount];
        var run = Guid.NewGuid().ToString("N");
        for (var user = 0; user < settings.Clients; user++)
        {
            var identity = environment.GetUserIdentity(user);
            for (var document = 0; document < settings.DocumentsPerClient; document++)
                requested[user * settings.DocumentsPerClient + document] = ($"/shared/users-{run}-{user}-{document}.docx", identity);
        }
        var setupStarted = Environment.TickCount64;
        Console.WriteLine($"Document user setup: importing {documentCount} documents for {settings.Clients} authenticated users.");
        var states = await DocumentLoadSeeder.SeedAsync(provider, template, requested, cancellationToken);
        Console.WriteLine($"Document user setup: catalog import completed in {Environment.TickCount64 - setupStarted} ms.");
        foreach (var state in states) httpDocuments.Add(new(state, cell));
        userSessions = new UserSession[settings.Clients];
        var initializedUsers = 0;
        // Setup stays bounded. Each signed-in account probes one active file, while its
        // remaining documents retain the verified immutable template graph until opened.
        await Parallel.ForEachAsync(Enumerable.Range(0, settings.Clients), new ParallelOptions
        {
            MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken,
        }, async (index, token) =>
        {
            var transport = new Session { Random = new(unchecked(settings.Seed + index * 397)) };
            sessions.Add(transport);
            transport.Http = await environment.CreateClientAsync(index, token);
            transport.Transport = new(transport.Http, new Uri(environment.HttpEndpoint, "/_vti_bin/cellstorage.svc"));
            var views = httpDocuments.Skip(index * settings.DocumentsPerClient).Take(settings.DocumentsPerClient)
                .Select(document => new UserView(new HttpView(document)
                {
                    Index = templateGraph.StorageIndex, Knowledge = initialKnowledge,
                }, templateGraph)).ToArray();
            var session = new UserSession(transport, views) { ActiveDocument = index % views.Length };
            await QueryUserAsync(session, session.Active, token);
            await DownloadUserAsync(session, session.Active, token);
            userSessions[index] = session;
            var completed = Interlocked.Increment(ref initializedUsers);
            if (completed % 250 == 0 || completed == settings.Clients)
                Console.WriteLine($"Document user setup: {completed}/{settings.Clients} account sessions and initial file probes ready.");
        });
        await ResetUserDatabaseStatisticsAsync();
        Console.WriteLine($"Document user workload: authenticated-users={environment.UserCount}, documents={documentCount}, " +
            $"protocol-concurrency={environment.ProtocolConcurrency}, protocol-concurrency-per-subject={environment.ProtocolConcurrencyPerSubject}, " +
            $"documents-per-user={settings.DocumentsPerClient}, package-bytes={payload.Length}, changed-xml-bytes={userCorpus.ChangedDocumentPartBytes}, " +
            $"save-interval-ms={settings.SaveIntervalMs}, query-interval-ms={settings.QueryIntervalMs}, open-interval-ms={settings.OpenIntervalMs}, " +
            $"offered-saves-per-second={settings.Clients * 1000d / settings.SaveIntervalMs:F2}. Setup probes one file per account.");
    }

    private async Task<IResponse> RunUsersAsync(IScenarioContext context)
    {
        var instance = context.ScenarioInfo.InstanceNumber;
        if (instance < 0 || instance >= userSessions.Length)
            throw new ArgumentException("Document Clients must cover the scenario's user instance numbers.");
        var user = userSessions[instance];
        if (context.ScenarioInfo.ScenarioOperation.ToString() != "Bombing")
            throw new ArgumentException("UserModel uses its own warm-up. Set NBomber WarmUpDuration to zero.");
        if (context.ScenarioInfo.ScenarioDuration.TotalMilliseconds < (long)settings.UserWarmUpMs + settings.UserMeasurementMs)
            throw new ArgumentException("The user simulation must cover its warm-up and measurement durations.");
        StartUserDatabaseSampling();
        Interlocked.CompareExchange(ref userMeasurementMilliseconds, settings.UserMeasurementMs, 0);
        Interlocked.CompareExchange(ref userMeasurementStart, Environment.TickCount64 + settings.UserWarmUpMs, 0);
        if (Interlocked.CompareExchange(ref user.Busy, 1, 0) != 0)
            throw new InvalidDataException("Two actions overlapped for the same signed-in user.");
        try
        {
            var now = Environment.TickCount64;
            if (!user.Started)
            {
                user.NextSave = now + user.Transport.Random.Next(settings.SaveIntervalMs);
                user.NextQuery = now + user.Transport.Random.Next(settings.QueryIntervalMs);
                user.NextOpen = now + user.Transport.Random.Next(settings.OpenIntervalMs);
                user.Started = true;
            }
            var action = user.NextSave <= user.NextQuery && user.NextSave <= user.NextOpen ? UserAction.Save
                : user.NextQuery <= user.NextOpen ? UserAction.Query : UserAction.Open;
            var due = action switch { UserAction.Save => user.NextSave, UserAction.Query => user.NextQuery, _ => user.NextOpen };
            var interval = action switch { UserAction.Save => settings.SaveIntervalMs, UserAction.Query => settings.QueryIntervalMs, _ => settings.OpenIntervalMs };
            var measurementEnd = userMeasurementStart + userMeasurementMilliseconds;
            if (due >= measurementEnd) return await FinishUserPhaseAsync(context);
            var delay = due - now;
            if (delay > 0)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(
                    context.ScenarioCancellationToken, resources.CancellationToken);
                try { await Task.Delay(TimeSpan.FromMilliseconds(delay), waiting.Token); }
                catch (OperationCanceledException) when (context.ScenarioCancellationToken.IsCancellationRequested &&
                    !resources.CancellationToken.IsCancellationRequested) { return Response.Ok(); }
            }
            if (Environment.TickCount64 >= measurementEnd) return await FinishUserPhaseAsync(context);
            var late = Math.Max(0, Environment.TickCount64 - due);
            Interlocked.Add(ref userLateStartMilliseconds, late);
            UserMaximum(ref userMaximumLateStartMilliseconds, late);
            Interlocked.Increment(ref userActions);
            try
            {
                switch (action)
                {
                    case UserAction.Save:
                        await SaveUserAsync(context, user);
                        break;
                    case UserAction.Query:
                        await HttpStepAsync(UserStepName("query"), context, async () =>
                        {
                            await QueryUserAsync(user, user.Active, resources.CancellationToken);
                            return true;
                        });
                        Interlocked.Increment(ref userQueries);
                        break;
                    case UserAction.Open:
                        user.ActiveDocument = (user.ActiveDocument + 1) % user.Views.Length;
                        await HttpStepAsync(UserStepName("open-download"), context, async () =>
                        {
                            await DownloadUserAsync(user, user.Active, resources.CancellationToken);
                            return true;
                        });
                        await HttpStepAsync(UserStepName("open-query"), context, async () =>
                        {
                            await QueryUserAsync(user, user.Active, resources.CancellationToken);
                            return true;
                        });
                        Interlocked.Increment(ref userOpens);
                        break;
                }
                return Response.Ok();
            }
            finally
            {
                // Advance from the scheduled time. Response time never becomes extra think time.
                // A blocked user skips elapsed slots instead of sending a catch-up request burst.
                var next = due + interval;
                var completed = Environment.TickCount64;
                if (next <= completed)
                {
                    var missed = (completed - next) / interval + 1;
                    Interlocked.Add(ref userMissedCadences, missed);
                    next += missed * interval;
                }
                switch (action)
                {
                    case UserAction.Save: user.NextSave = next; break;
                    case UserAction.Query: user.NextQuery = next; break;
                    case UserAction.Open: user.NextOpen = next; break;
                }
            }
        }
        finally { Volatile.Write(ref user.Busy, 0); }
    }

    private async Task SaveUserAsync(IScenarioContext context, UserSession user)
    {
        var view = user.Active;
        var revision = Interlocked.Increment(ref nextUserRevision);
        var save = userCorpus.CreateSave(view.Graph, revision);
        if (save.ReusedChunks == 0 || save.SentChunks != 1 || save.ChangedChunkBytes >= payload.Length / 2)
            throw new InvalidDataException("A user edit did not reuse the unchanged package parts.");
        var proposed = ((PutChangesSubRequestData)save.Request.SubRequests.Single().Data!).StorageIndex;
        var hash = Convert.ToHexString(SHA256.HashData(save.Content));
        Interlocked.Increment(ref userSaveAttempts);
        Interlocked.Add(ref userSaveBytes, save.Binary.Length);
        Interlocked.Add(ref userChangedChunkBytes, save.ChangedChunkBytes);
        UserMaximum(ref userMaximumSaveBytes, save.Binary.Length);
        UserMinimum(ref userMinimumSaveBytes, save.Binary.Length);
        var requestStarted = Environment.TickCount64;
        var measuredRequest = requestStarted >= userMeasurementStart && requestStarted < userMeasurementStart + userMeasurementMilliseconds;
        if (measuredRequest) Interlocked.Increment(ref userMeasuredSaveStarts);
        await SaveHttpStepAsync(UserStepName("save"), context, user.Transport, view.Wire, save.Request, save.Binary, false, resources.CancellationToken);
        if (!view.Wire.Document.Committed.TryAdd(proposed, hash))
            throw new InvalidDataException("A user edit reused a previously acknowledged storage index.");
        Interlocked.Increment(ref commits);
        var completed = Environment.TickCount64;
        if (measuredRequest)
        {
            if (completed < userMeasurementStart + userMeasurementMilliseconds) Interlocked.Increment(ref userMeasuredCommits);
            else Interlocked.Increment(ref userMeasuredLateAcknowledgements);
        }
        view.Saves++;
        view.Graph = view.Graph.Merge(save.Request.DataElementPackage!.DataElements, proposed);
        if (settings.Retry)
        {
            await SaveHttpStepAsync("replay", context, user.Transport, view.Wire, save.Request, save.Binary, true, resources.CancellationToken);
            Interlocked.Increment(ref replays);
        }
    }

    private static async Task QueryUserAsync(UserSession session, UserView view, CancellationToken cancellationToken)
    {
        var response = await QueryHttpAsync(session.Transport, view.Wire, cancellationToken);
        if (response.DataElementPackage is { DataElements.Count: > 0 } package)
            view.Graph = view.Graph.Merge(package.DataElements, view.Wire.Index);
        else if (!view.Graph.StorageIndex.Equals(view.Wire.Index))
            throw new InvalidDataException("The user query advanced its index without supplying the missing graph.");
    }

    private async Task DownloadUserAsync(UserSession session, UserView view, CancellationToken cancellationToken)
    {
        var document = view.Wire.Document;
        if (!view.Wire.Index.Equals(view.Graph.StorageIndex))
            throw new InvalidDataException("The opened file's client graph does not match its known index.");
        string expected;
        if (document.Committed.TryGetValue(view.Wire.Index, out var hash)) expected = hash;
        else
        {
            var initialIndex = document.Initial.Partitions.Single(partition => partition.Kind == 0).StorageIndex!;
            if (!view.Wire.Index.Equals(StorageIds.Restore(initialIndex)))
                throw new InvalidDataException("The opened file has no acknowledged content hash.");
            expected = document.Initial.Content.Sha256;
        }
        var bytes = await session.Transport.Http!.GetByteArrayAsync(document.Initial.Path, cancellationToken);
        if (bytes.Length != payload.Length || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The opened file differs from its acknowledged package.");
    }

    private void PrintUserMetrics()
    {
        var attempts = Interlocked.Read(ref userSaveAttempts);
        var actions = Interlocked.Read(ref userActions);
        Console.WriteLine($"Document bounded measurement: active-users={userSessions.Count(user => user.Started)}, window-ms={userMeasurementMilliseconds}, " +
            $"save-starts={userMeasuredSaveStarts}, acknowledged-commits={userMeasuredCommits}, late-acknowledgements={userMeasuredLateAcknowledgements}, " +
            $"commits-per-minute={(userMeasurementMilliseconds == 0 ? 0 : userMeasuredCommits * 60_000d / userMeasurementMilliseconds):F2}. " +
            "Commits must be acknowledged inside this window; NBomber step totals can include retiring invocations.");
        Console.WriteLine($"Document user totals including warm-up: save-attempts={attempts}, acknowledged-saves={commits}, " +
            $"queries={userQueries}, opens={userOpens}, serialized-fsshttpb-save-bytes={userSaveBytes}, changed-zip-chunk-bytes={userChangedChunkBytes}, " +
            $"mean-save-bytes={(attempts == 0 ? 0 : userSaveBytes / attempts)}, " +
            $"min-save-bytes={(attempts == 0 ? 0 : userMinimumSaveBytes)}, max-save-bytes={userMaximumSaveBytes}, " +
            $"mean-changed-chunk-bytes={(attempts == 0 ? 0 : userChangedChunkBytes / attempts)}, missed-cadence-slots={userMissedCadences}, " +
            $"mean-action-start-lateness-ms={(actions == 0 ? 0 : userLateStartMilliseconds / actions)}, max-action-start-lateness-ms={userMaximumLateStartMilliseconds}.");
    }

    private string UserStepName(string name) => Environment.TickCount64 < userMeasurementStart ? "warmup-" + name : name;

    private static async Task<IResponse> FinishUserPhaseAsync(IScenarioContext context)
    {
        try { await Task.Delay(Timeout.Infinite, context.ScenarioCancellationToken); }
        catch (OperationCanceledException) when (context.ScenarioCancellationToken.IsCancellationRequested) { }
        return Response.Ok();
    }

    private static void UserMaximum(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) break;
            current = observed;
        }
    }

    private static void UserMinimum(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value < current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) break;
            current = observed;
        }
    }

    private enum UserAction { Save, Query, Open }

    private sealed class UserSession(Session transport, UserView[] views)
    {
        public Session Transport { get; } = transport;
        public UserView[] Views { get; } = views;
        public UserView Active => Views[ActiveDocument];
        public int ActiveDocument;
        public int Busy;
        public bool Started;
        public long NextSave;
        public long NextQuery;
        public long NextOpen;
    }

    private sealed class UserView(HttpView wire, PartitionGraphSnapshot graph)
    {
        public HttpView Wire { get; } = wire;
        public PartitionGraphSnapshot Graph = graph;
        public int Saves;
    }
}
