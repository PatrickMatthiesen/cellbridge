using System.Globalization;
using System.Net;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;

namespace CellBridge.Client;

/// <summary>Maintains the protocol state for one open Office document.</summary>
public sealed class CellBridgeDocumentSession : IAsyncDisposable
{
    public const string ExcelSchemaLockId = "{29358EC1-E813-4793-8E70-ED0344E7B73C}";

    private const string EndpointProperty = "RealtimeTypingEndpointUrl";
    private const string TokenProperty = "DocumentAccessToken";
    private const string TokenTtlProperty = "DocumentAccessTokenTtl";

    private readonly ICellBridgeClientTransport _transport;
    private readonly CellBridgeDocumentSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<string> _notifications = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly object _closeGate = new();
    private Task? _close;
    private Task? _leaseRenewal;
    private Task? _realTimeWork;
    private ICellBridgeRealTimeChannel? _realTime;
    private bool _readerMayExist;
    private bool _writerMayExist;
    private bool _joinedAsWriter;
    private int _closing;
    private SessionState? _state;
    private PendingSave? _pending;

    private CellBridgeDocumentSession(ICellBridgeClientTransport transport, Uri documentUri,
        CellBridgeDocumentSessionOptions options)
    {
        _transport = transport;
        _options = options;
        DocumentUri = documentUri;
    }

    public Uri DocumentUri { get; private set; }
    public Guid ResourceId { get; private set; }
    public Guid ClientId { get; } = Guid.NewGuid();
    public string? UserLogin { get; private set; }

    public static async Task<CellBridgeDocumentSession> OpenAsync(ICellBridgeClientTransport transport, Uri documentUri,
        CellBridgeDocumentSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(documentUri);
        if (!documentUri.IsAbsoluteUri || documentUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("An absolute HTTP(S) document URI is required.", nameof(documentUri));

        options ??= new();
        ValidateOptions(options);
        var session = new CellBridgeDocumentSession(transport, documentUri, options);
        try
        {
            await session.DiscoverAsync(cancellationToken);
            if (options.Mode == CellBridgeDocumentSessionMode.EagerWriter)
            {
                await session.JoinWriterAsync(cancellationToken);
            }
            else
            {
                session._readerMayExist = true;
                await session.SendEditorsAsync("JoinEditingSession", cancellationToken);
            }

            RealTimeConnectionTarget? realTimeTarget = null;
            if (options.EnableRealTime)
            {
                try { realTimeTarget = await session.GetRealTimeTargetAsync(cancellationToken); }
                catch (Exception error) when (IsAmbiguous(error) && !cancellationToken.IsCancellationRequested)
                {
                    // RTC discovery is optional. Continue with protocol sync.
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await session.SyncCoreAsync(cancellationToken);
            if (realTimeTarget is { } target)
            {
                try
                {
                    session._realTime = await transport.TryConnectRealTimeAsync(target, cancellationToken);
                }
                catch (Exception error) when (IsAmbiguous(error) && !cancellationToken.IsCancellationRequested)
                {
                    // RTC is optional. Protocol sync remains available after a connection failure.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            session._leaseRenewal = session.RenewLeaseAsync();
            if (session._realTime is { } channel && realTimeTarget is { } connectedTarget)
            {
                session._realTimeWork = session.RunRealTimeAsync(channel, connectedTarget.ExpiresAt);
            }
            else
            {
                session._notifications.Writer.TryComplete();
            }
            return session;
        }
        catch (Exception openError)
        {
            try { await session.DisposeAsync(); }
            catch (Exception closeError) { throw new AggregateException(openError, closeError); }
            throw;
        }
    }

    /// <summary>Returns the current revision and a detached copy of its Office package.</summary>
    public CellBridgeDocumentSnapshot GetSnapshot()
    {
        ThrowIfClosing();
        return CopySnapshot(State);
    }

    /// <summary>Returns the editor entries from the last synchronized editors-table partition.</summary>
    public IReadOnlyList<EditorsTableEditor> GetEditors()
    {
        ThrowIfClosing();
        var stream = State.Editors.ReadFile().Materialize(_options.MaxPackageBytes);
        return EditorsTableStreamParser.Parse(stream);
    }

    /// <summary>Reads coalesced RTC notifications. Call SyncAsync to obtain the current graph.</summary>
    public IAsyncEnumerable<string> ReadNotificationsAsync(CancellationToken cancellationToken = default) =>
        _notifications.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Queries current partition graphs and publishes their state together.</summary>
    public async Task<CellBridgeDocumentSnapshot> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfClosing();
            if (_pending is not null)
                throw new InvalidOperationException("Retry the unresolved save with its original revision and package before syncing.");
            return CopySnapshot(await SyncCoreAsync(cancellationToken));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Attempts to commit one package against the supplied revision.</summary>
    /// <remarks>If the response is lost, retry with the same revision and package. If the replay returns
    /// <see cref="CellBridgeCommitStatus.OutcomeUnknown"/>, sync and inspect the current document before editing.</remarks>
    public async Task<CellBridgeCommitResult> TryCommitAsync(string expectedRevision, ReadOnlyMemory<byte> package,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        if (package.Length > _options.MaxPackageBytes)
            throw new ArgumentException("The package exceeds the configured size limit.", nameof(package));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfClosing();
            if (_options.Mode == CellBridgeDocumentSessionMode.Reader)
                throw new InvalidOperationException("A reader session cannot commit changes.");

            var current = State;
            if (_pending is { } existing && (existing.Revision != expectedRevision ||
                !package.Span.SequenceEqual(existing.Package)))
                throw new InvalidOperationException("A save is unresolved. Retry it with its original revision and package.");
            if (expectedRevision != current.Revision)
                return new(CellBridgeCommitStatus.Conflict, current.Revision);

            var pending = _pending;
            if (pending is null)
            {
                var bytes = package.ToArray();
                var binary = current.File.CreateSave(current.FileCell, bytes,
                    _options.MaxGraphBytes - current.Metadata.Size - current.Editors.Size);
                var request = CreateRequest(CreateCellRequest(Guid.Empty, binary, writing: true));
                pending = new(expectedRevision, bytes, binary, request);
            }

            if (!_joinedAsWriter)
                await JoinWriterAsync(cancellationToken);
            _pending = pending;
            return await ExecuteSaveAsync(current, pending, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal bool HasPendingCommit(string revision, byte[] package) =>
        Volatile.Read(ref _pending) is { } pending && pending.Revision == revision &&
        package.AsSpan().SequenceEqual(pending.Package);

    public ValueTask DisposeAsync()
    {
        lock (_closeGate)
        {
            Volatile.Write(ref _closing, 1);
            return new(_close ??= CloseAsync());
        }
    }

    private async Task<CellBridgeCommitResult> ExecuteSaveAsync(SessionState current, PendingSave pending,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var outer = await SendSingleAsync(pending.Request, cancellationToken);
                var (response, operation) = ReadCell(outer, RequestTypes.PutChanges);
                if (operation is { Status: true, Error: { Type: ErrorType.Cell, ErrorCode: (ulong)CellErrorCode.CoherencyFailure } })
                {
                    // A newer save can supersede an accepted receipt before we receive its response.
                    _pending = null;
                    return new(pending.HadAmbiguousResponse ? CellBridgeCommitStatus.OutcomeUnknown :
                        CellBridgeCommitStatus.Conflict, current.Revision);
                }

                CheckCellError(operation.Status, operation.Error);
                var save = operation.Data as PutChangesSubResponseData ??
                    throw new InvalidDataException("Save response did not include PutChanges data.");
                var updated = current.File.ApplySave(pending.Binary, response, save,
                    _options.MaxGraphBytes - current.Metadata.Size - current.Editors.Size);
                var graph = updated.ReadFile();
                var materialized = graph.Materialize(_options.MaxPackageBytes);
                if (!materialized.AsSpan().SequenceEqual(pending.Package))
                    throw new InvalidDataException("The applied graph does not reproduce the proposed package.");

                var next = current with { File = updated, FileCell = graph.FileCell, Package = pending.Package };
                Volatile.Write(ref _state, next);
                _pending = null;
                return new(CellBridgeCommitStatus.Committed, next.Revision);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                // CellStorage rejects the request body before executing its subrequests.
                if (pending.HadAmbiguousResponse)
                    throw UnknownSaveOutcome(error);
                _pending = null;
                throw;
            }
            catch (CellBridgeProtocolException error) when (!IsAmbiguous(error) && !error.IsSoapFault)
            {
                if (error.ErrorCode == "InvalidCoauthSession")
                    _joinedAsWriter = false;
                if (pending.HadAmbiguousResponse)
                    throw UnknownSaveOutcome(error);
                _pending = null;
                throw;
            }
            catch (Exception error)
            {
                // A failure without a definitive rejection can follow a committed save.
                pending.HadAmbiguousResponse = true;
                if (error is OutOfMemoryException ||
                    error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                if (attempt != 0 || cancellationToken.IsCancellationRequested)
                    throw UnknownSaveOutcome(error);
                // Resubmit the original request object, including its proposal ID and binary payload.
            }
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var request = CreateRequest(
            new FssHttpSubRequest { Type = SubRequestType.WhoAmI },
            new FssHttpSubRequest { Type = SubRequestType.GetDocMetaInfo });
        var response = await SendAsync(request, cancellationToken);
        var identity = FindResponse(response, request.Requests[0].SubRequests[0]);
        FindResponse(response, request.Requests[0].SubRequests[1]);
        if (response.ResourceId is not { } resource || resource == Guid.Empty ||
            !Uri.TryCreate(DocumentUri, response.Url, out var canonicalUri) ||
            canonicalUri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(response.Url))
            throw new InvalidDataException("Document discovery omitted its canonical URL or resource ID.");

        ResourceId = resource;
        DocumentUri = canonicalUri;
        UserLogin = identity.SubResponseDataAttributes.GetValueOrDefault("UserLogin");
    }

    private async Task JoinWriterAsync(CancellationToken cancellationToken)
    {
        bool mayHaveWriter = _writerMayExist;
        _writerMayExist = true;
        try
        {
            await SendCoauthAsync("JoinCoauthoring", cancellationToken);
            _joinedAsWriter = true;
        }
        catch (CellBridgeProtocolException error) when (error.ErrorCode is not ("CellRequestFail" or "SubRequestFail"))
        {
            _writerMayExist = mayHaveWriter;
            throw;
        }
    }

    private Task<FssHttpSubResponse> SendCoauthAsync(string operation, CancellationToken cancellationToken)
    {
        var request = new FssHttpSubRequest
        {
            Type = SubRequestType.Coauth,
            SubRequestDataAttributes =
            {
                ["CoauthRequestType"] = operation,
                ["ClientID"] = ClientId.ToString("B"),
                ["SchemaLockID"] = ExcelSchemaLockId,
            },
        };
        if (operation != "ExitCoauthoring")
            request.SubRequestDataAttributes["Timeout"] = _options.LeaseTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        return SendSingleAsync(CreateRequest(request), cancellationToken);
    }

    private Task<FssHttpSubResponse> SendEditorsAsync(string operation, CancellationToken cancellationToken)
    {
        var request = new FssHttpSubRequest
        {
            Type = SubRequestType.EditorsTable,
            SubRequestDataAttributes =
            {
                ["EditorsTableRequestType"] = operation,
                ["ClientID"] = ClientId.ToString("B"),
            },
        };
        if (operation != "LeaveEditingSession")
        {
            request.SubRequestDataAttributes["Timeout"] = _options.LeaseTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            request.SubRequestDataAttributes["AsEditor"] = "false";
        }
        return SendSingleAsync(CreateRequest(request), cancellationToken);
    }

    private async Task<RealTimeConnectionTarget?> GetRealTimeTargetAsync(CancellationToken cancellationToken)
    {
        string[] keys = [EndpointProperty, TokenProperty, TokenTtlProperty];
        XNamespace protocol = CellStorageRequest.Namespace;
        var request = new FssHttpSubRequest
        {
            Type = SubRequestType.Properties,
            SubRequestDataAttributes = { ["Properties"] = "PropertyGet" },
            SubRequestDataXml = new XElement(protocol + "SubRequestData", new XElement(protocol + "PropertyIds",
                keys.Select(key => new XElement(protocol + "PropertyId", new XAttribute("id", key))))).ToString(SaveOptions.DisableFormatting),
        };
        FssHttpSubResponse response;
        try { response = await SendSingleAsync(CreateRequest(request), cancellationToken); }
        catch (CellBridgeProtocolException error) when (error.ErrorCode == "RequestNotSupported") { return null; }

        if (response.SubResponseDataXml is not { } xml)
            return null;
        var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var element in XElement.Parse(xml).Elements())
        {
            if (element.Name.LocalName == "PropertyValue" && (string?)element.Attribute("id") is { } id &&
                keys.Contains(id, StringComparer.Ordinal) && !properties.TryAdd(id, (string?)element.Attribute("value")))
                throw new InvalidDataException($"The RTC properties response duplicated '{id}'.");
        }
        if (!properties.TryGetValue(EndpointProperty, out var address) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https" or "ws" or "wss") ||
            !properties.TryGetValue(TokenProperty, out var token) || string.IsNullOrWhiteSpace(token))
            return null;

        DateTimeOffset? expires = null;
        if (long.TryParse(properties.GetValueOrDefault(TokenTtlProperty), NumberStyles.None,
            CultureInfo.InvariantCulture, out var milliseconds) && milliseconds is > 0 and <= 253402300799999)
            expires = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return new(endpoint, token, expires, ClientId.ToString("B"));
    }

    private async Task<SessionState> SyncCoreAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _state);
        var file = await QueryPartitionAsync(Guid.Empty, current?.File, _options.MaxGraphBytes -
            (current?.Metadata.Size ?? 0) - (current?.Editors.Size ?? 0), cancellationToken);
        var metadata = await QueryPartitionAsync(StoredDocument.MetadataPartitionId, current?.Metadata,
            _options.MaxGraphBytes - file.Size - (current?.Editors.Size ?? 0), cancellationToken);
        // Editor graphs are generated per membership snapshot. Start with empty
        // knowledge so new snapshots can replace old ones without retaining history.
        var editors = await QueryPartitionAsync(StoredDocument.EditorsTablePartitionId, null,
            _options.MaxGraphBytes - file.Size - metadata.Size, cancellationToken);
        SessionState next;
        if (current is not null && ReferenceEquals(current.File.Index, file.Index))
        {
            next = current with { File = file, Metadata = metadata, Editors = editors };
        }
        else
        {
            var graph = file.ReadFile();
            var package = graph.Materialize(_options.MaxPackageBytes);
            if (current is not null && current.File.Index.DataElementExtendedGuid.Equals(file.Index.DataElementExtendedGuid) &&
                !package.AsSpan().SequenceEqual(current.Package))
                throw new InvalidDataException("Query returned different package bytes under the applied storage index.");
            next = new(file, metadata, editors, graph.FileCell, package);
        }
        Volatile.Write(ref _state, next);
        return next;
    }

    private async Task<DocumentSessionGraph> QueryPartitionAsync(Guid partition, DocumentSessionGraph? current,
        long maxGraphBytes, CancellationToken cancellationToken)
    {
        var pages = new DocumentSessionGraph.QueryPages(current, maxGraphBytes);
        var knowledge = current?.Knowledge;
        for (int page = 0; page < _options.MaxQueryPages; page++)
        {
            var binary = new FsshttpbCellRequest
            {
                SubRequests =
                {
                    new(RequestTypes.QueryChanges)
                    {
                        RequestId = 1,
                        Data = new QueryChangesSubRequestData
                        {
                            IncludeStorageManifest = true,
                            IncludeCellChanges = true,
                            Knowledge = knowledge,
                            MaxDataElements = _options.QueryPageBytes,
                        },
                    },
                },
            };
            var outer = await SendSingleAsync(CreateRequest(CreateCellRequest(partition, binary, writing: false)), cancellationToken);
            var (response, operation) = ReadCell(outer, RequestTypes.QueryChanges);
            CheckCellError(operation.Status, operation.Error);
            var query = operation.Data as QueryChangesSubResponseData ??
                throw new InvalidDataException("Query response did not include QueryChanges data.");

            bool progress = pages.Stage(response.DataElementPackage?.DataElements ?? []);
            var nextKnowledge = DocumentSessionGraph.ReadKnowledge(query.KnowledgeBytes);
            if (!query.PartialResult)
                return pages.Complete(query.StorageIndexExtendedGuid, nextKnowledge);
            if (nextKnowledge.RequiresFullResponse ||
                !progress && knowledge is not null && nextKnowledge.Ranges.SequenceEqual(knowledge.Ranges))
                throw new InvalidDataException("A partial query cannot advance its data element knowledge.");
            knowledge = nextKnowledge;
        }

        throw new InvalidDataException("Query exceeded the configured page limit.");
    }

    private async Task RenewLeaseAsync()
    {
        var cancellationToken = _lifetime.Token;
        using var timer = new PeriodicTimer(_options.LeaseRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (_writerMayExist)
                    {
                        await SendCoauthAsync("RefreshCoauthoring", cancellationToken);
                        _joinedAsWriter = true;
                    }
                    else
                    {
                        try { await SendEditorsAsync("RefreshEditingSession", cancellationToken); }
                        catch (CellBridgeProtocolException error) when (error.ErrorCode == "EditorClientIdNotFound")
                        {
                            await SendEditorsAsync("JoinEditingSession", cancellationToken);
                        }
                    }
                }
                catch (Exception error) when (error is CellBridgeProtocolException || IsAmbiguous(error))
                {
                    // The next tick retries the lease; a commit still requires server admission.
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunRealTimeAsync(ICellBridgeRealTimeChannel channel, DateTimeOffset? expiry)
    {
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var pump = PumpNotificationsAsync(channel, connectionLifetime);
        await Task.WhenAll(pump, RenewRealTimeAsync(channel, expiry, connectionLifetime.Token));
    }

    private async Task RenewRealTimeAsync(ICellBridgeRealTimeChannel channel, DateTimeOffset? expiry,
        CancellationToken cancellationToken)
    {
        var delay = RealTimeRenewalDelay(expiry);
        try
        {
            while (true)
            {
                await Task.Delay(delay, cancellationToken);
                try
                {
                    // Properties uses the session's fixed document and client IDs and does not change its lease or graphs.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await GetRealTimeTargetAsync(cancellationToken) is { } target &&
                        (target.ExpiresAt is null || target.ExpiresAt > DateTimeOffset.UtcNow))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await channel.UpdateClientInfoAsync(target, cancellationToken);
                        if (target.ExpiresAt is null || target.ExpiresAt != expiry)
                        {
                            expiry = target.ExpiresAt;
                            delay = RealTimeRenewalDelay(expiry);
                            continue;
                        }
                    }
                }
                catch (Exception error) when (error is CellBridgeProtocolException || IsAmbiguous(error))
                {
                    // A failed ticket update can be retried without refreshing the editor lease.
                }
                var retryDelay = RealTimeRenewalDelay(expiry);
                delay = retryDelay > TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(Math.Clamp(retryDelay.TotalMilliseconds, 100, 5000))
                    : _options.LeaseRenewalInterval;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private TimeSpan RealTimeRenewalDelay(DateTimeOffset? expiry)
    {
        if (expiry is null)
            return _options.LeaseRenewalInterval;

        // Renew halfway through the remaining lifetime to leave time for delays and retries.
        var remaining = expiry.Value - DateTimeOffset.UtcNow;
        return TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds / 2, 0, uint.MaxValue - 1));
    }

    private async Task PumpNotificationsAsync(ICellBridgeRealTimeChannel channel, CancellationTokenSource connectionLifetime)
    {
        var cancellationToken = connectionLifetime.Token;
        Exception? failure = null;
        try
        {
            await foreach (var notification in channel.ReadNotificationsAsync(cancellationToken).WithCancellation(cancellationToken))
                _notifications.Writer.TryWrite(notification);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            try { await connectionLifetime.CancelAsync(); }
            finally { _notifications.Writer.TryComplete(failure); }
        }
    }

    private async Task CloseAsync()
    {
        var failures = new List<Exception>();
        try { await _lifetime.CancelAsync(); }
        catch (Exception error) { failures.Add(error); }
        if (_leaseRenewal is { } renewal)
        {
            try { await renewal; }
            catch (Exception error) { failures.Add(error); }
        }
        if (_realTimeWork is { } realTimeWork)
        {
            try { await realTimeWork; }
            catch (Exception error) { failures.Add(error); }
        }

        await _gate.WaitAsync();
        try
        {
            if (_realTime is { } channel)
            {
                try { await channel.DisposeAsync(); }
                catch (Exception error) { failures.Add(error); }
            }
            _notifications.Writer.TryComplete();

            if (_writerMayExist)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await SendCoauthAsync("ExitCoauthoring", timeout.Token); }
                catch (CellBridgeProtocolException error) when (error.ErrorCode == "FileNotLockedOnServer" ||
                    CleanupDenied(error)) { }
                catch (Exception error) { failures.Add(error); }
            }
            if (_readerMayExist || _writerMayExist)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await SendEditorsAsync("LeaveEditingSession", timeout.Token); }
                catch (CellBridgeProtocolException error) when (CleanupDenied(error)) { }
                catch (Exception error) { failures.Add(error); }
            }

            _pending = null;
            Volatile.Write(ref _state, null);
        }
        finally
        {
            _gate.Release();
            _lifetime.Dispose();
        }

        if (failures.Count > 0)
            throw new AggregateException("Document session cleanup failed.", failures);
    }

    private FssHttpSubRequest CreateCellRequest(Guid partition, FsshttpbCellRequest binary, bool writing)
    {
        var payload = binary.ToByteArray();
        var request = new FssHttpSubRequest
        {
            Type = SubRequestType.Cell,
            SubRequestDataBinary = payload,
            SubRequestDataAttributes =
            {
                ["PartitionID"] = partition.ToString("B"),
                ["BinaryDataSize"] = payload.Length.ToString(CultureInfo.InvariantCulture),
            },
        };
        if (writing)
        {
            request.SubRequestDataAttributes["SchemaLockID"] = ExcelSchemaLockId;
            request.SubRequestDataAttributes["ClientID"] = ClientId.ToString("B");
        }
        return request;
    }

    private CellStorageRequest CreateRequest(params FssHttpSubRequest[] subrequests)
    {
        for (int index = 0; index < subrequests.Length; index++)
            subrequests[index].SubRequestToken = (ulong)index + 1;
        return new()
        {
            MinorVersion = 2,
            CorrelationId = Guid.NewGuid(),
            Requests =
            {
                new()
                {
                    RequestToken = 1,
                    Url = DocumentUri.AbsoluteUri,
                    ResourceId = ResourceId == Guid.Empty ? null : ResourceId.ToString("D"),
                    UseResourceId = ResourceId != Guid.Empty,
                    SubRequests = [.. subrequests],
                },
            },
        };
    }

    private async Task<FssHttpResponse> SendAsync(CellStorageRequest request, CancellationToken cancellationToken)
    {
        var execution = await _transport.ExecuteAsync(request, cancellationToken);
        if (execution.IsCancelled)
            throw new OperationCanceledException("The response is incomplete.", cancellationToken);
        if (execution.Response.Fault is { } fault)
            throw new CellBridgeProtocolException(fault.Code ?? "SoapFault", fault.Reason) { IsSoapFault = true };
        CheckError(execution.Response.VersionErrorCode, execution.Response.VersionErrorMessage);
        var response = MatchResponse(execution.Response.Responses.Where(response =>
            response.RequestToken == request.Requests[0].RequestToken), "response token");
        CheckError(response.ErrorCode, response.ErrorMessage);
        return response;
    }

    private async Task<FssHttpSubResponse> SendSingleAsync(CellStorageRequest request, CancellationToken cancellationToken) =>
        FindResponse(await SendAsync(request, cancellationToken), request.Requests[0].SubRequests[0]);

    private static FssHttpSubResponse FindResponse(FssHttpResponse response, FssHttpSubRequest request)
    {
        var operation = MatchResponse(response.SubResponses.Where(operation =>
            operation.Type == request.Type && operation.SubRequestToken == request.SubRequestToken), "subresponse token and type");
        CheckError(operation.ErrorCode, operation.ErrorMessage);
        return operation;
    }

    private static (FsshttpbResponse, FsshttpbSubResponse) ReadCell(FssHttpSubResponse outer, RequestTypes type)
    {
        if (outer.SubResponseStreamInvalid)
            throw new InvalidDataException("The server marked the Cell subresponse stream invalid.");
        var response = outer.BinaryResponse ?? FsshttpbResponse.FromBytes(
            outer.SubResponseDataBase64 ?? throw new InvalidDataException("The Cell subresponse has no binary data."));
        CheckCellError(response.Status, response.Error);
        var operation = MatchResponse(response.SubResponses.Where(sub => sub.RequestId == 1 && sub.RequestType == type),
            "Cell operation");
        return (response, operation);
    }

    private static T MatchResponse<T>(IEnumerable<T> matches, string field)
    {
        using var enumerator = matches.GetEnumerator();
        if (!enumerator.MoveNext())
            throw new InvalidDataException($"The server omitted the requested {field}.");
        var match = enumerator.Current;
        if (enumerator.MoveNext())
            throw new InvalidDataException($"The server duplicated the requested {field}.");
        return match;
    }

    private static void CheckCellError(bool failed, ResponseError? error)
    {
        if (failed)
        {
            if (error is null)
                throw new InvalidDataException("The binary error response omitted its error code.");
            var code = error.Type == ErrorType.Cell && error.ErrorCode <= int.MaxValue
                ? ((CellErrorCode)error.ErrorCode).ToString() : $"{error.Type} {error.ErrorCode}";
            throw new CellBridgeProtocolException(code, error.ErrorMessage);
        }
    }

    private static void CheckError(string? code, string? detail)
    {
        if (code is not (null or "Success"))
            throw new CellBridgeProtocolException(code, detail);
    }

    private static bool IsAmbiguous(Exception error) =>
        error is HttpRequestException or IOException or TimeoutException or OperationCanceledException
            or InvalidDataException or XmlException or FormatException ||
        error is CellBridgeProtocolException { ErrorCode: "CellRequestFail" or "SubRequestFail" };

    private static IOException UnknownSaveOutcome(Exception error) =>
        new("The save outcome is unknown. Retry with the original revision and package.", error);

    // A deleted document or revoked read grant can prevent release. Local ACL
    // changes remove the affected membership, and other leases expire server-side.
    private static bool CleanupDenied(CellBridgeProtocolException error) =>
        error.ErrorCode is "FileUnauthorizedAccess" or "FileNotExistsOrCannotBeCreated";

    private static void ValidateOptions(CellBridgeDocumentSessionOptions options)
    {
        if (!Enum.IsDefined(options.Mode) || options.LeaseTimeoutSeconds is < 60 or > 120000 ||
            options.LeaseRenewalInterval <= TimeSpan.Zero ||
            options.LeaseRenewalInterval >= TimeSpan.FromSeconds(Math.Max(3600, options.LeaseTimeoutSeconds)) ||
            options.QueryPageBytes == 0 || options.MaxQueryPages <= 0 ||
            options.MaxPackageBytes <= 0 || options.MaxGraphBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid session limits or lease settings.");
    }

    private void ThrowIfClosing() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);

    private SessionState State => Volatile.Read(ref _state) ?? throw new ObjectDisposedException(nameof(CellBridgeDocumentSession));

    private static CellBridgeDocumentSnapshot CopySnapshot(SessionState state) => new(state.Revision, state.Package.ToArray());

    private sealed record SessionState(DocumentSessionGraph File, DocumentSessionGraph Metadata,
        DocumentSessionGraph Editors, CellId FileCell, byte[] Package)
    {
        internal string Revision => File.Index.DataElementExtendedGuid.ToString();
    }

    private sealed record PendingSave(string Revision, byte[] Package, FsshttpbCellRequest Binary, CellStorageRequest Request)
    {
        internal bool HadAmbiguousResponse { get; set; }
    }
}
