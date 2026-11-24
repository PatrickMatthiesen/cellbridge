using CellBridge.AspNetCore;
using CellBridge.Client;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using System.Net;
using System.Security.Cryptography;

namespace CellBridge.Mcp.AspNetCore;

internal sealed class McpWorkbookRegistry(
    CellBridgeInProcessHost host, CellBridgeMcpOptions options, CellBridgeOptions protocolOptions,
    TimeProvider clock, ILogger<McpWorkbookRegistry> logger)
    : BackgroundService
{
    private const int HandleBytes = 32;
    private static readonly TimeSpan ReleaseRetryDelay = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly Dictionary<string, McpWorkbookEntry> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<WorkbookKey, McpWorkbookEntry> _workbooks = [];
    private readonly int _leaseTimeoutSeconds = (int)Math.Clamp(Math.Ceiling(options.IdleSessionTimeout.TotalSeconds), 60, 120000);
    private bool _stopping;

    public int MaxReadCells => options.MaxReadCells;

    public int MaxOfficeReadItems => Math.Min(options.MaxReadCells, 1000);

    public async Task<McpOpenDocument> OpenAsync(McpRequestIdentity identity, DocumentIdentity target,
        McpDocumentFormat format, CancellationToken cancellationToken)
    {
        var invocation = ResolveInvocation(identity);
        using var deadline = invocation.CreateDeadline();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline?.Token ?? default);
        var requestToken = lifetime.Token;
        while (true)
        {
            var document = await host.ResolveDocumentAsync(target, invocation.Actor, invocation.PublicOrigin,
                invocation.PathBase, DocumentAccess.Read, requestToken) ?? throw Unavailable(format);
            if (!MatchesPath(document.Path, format))
                throw Unavailable(format);
            var key = new WorkbookKey(invocation.Actor.Identity.Subject, invocation.SessionId, document.ResourceId);
            McpWorkbookEntry entry;
            bool opening;
            lock (_gate)
            {
                ThrowIfStopping();
                opening = !_workbooks.TryGetValue(key, out entry!);
                if (opening)
                {
                    if (_handles.Count >= options.MaxTotalSessions ||
                        _workbooks.Keys.Count(candidate => candidate.Subject == key.Subject) >=
                        options.MaxConcurrentSessionsPerActor)
                        throw new McpException(format == McpDocumentFormat.Excel
                            ? "The open workbook session limit was reached."
                            : "The open document session limit was reached.");
                    entry = new McpWorkbookEntry(key, format, document.LifecycleGeneration,
                        invocation.ExpiresAt, options.IdleSessionTimeout, clock.GetUtcNow(),
                        Convert.ToHexString(RandomNumberGenerator.GetBytes(HandleBytes)),
                        new McpWorkbookTransport(host, key.ResourceId, document.LifecycleGeneration, key.Subject,
                            _leaseTimeoutSeconds, clock));
                    entry.Gate.Wait(0);
                    _workbooks.Add(key, entry);
                    _handles.Add(entry.Handle, entry);
                }
            }

            if (!opening)
                await WaitForRequestAsync(entry, requestToken);

            try
            {
                requestToken.ThrowIfCancellationRequested();
                if (entry.Closing)
                    throw Unavailable(format);
                if (entry.Removed)
                    continue;
                if (entry.Closed || entry.Format != format)
                    throw Unavailable(format);
                if (entry.LifecycleGeneration != document.LifecycleGeneration || entry.IsExpired(clock.GetUtcNow()))
                {
                    if (!await CloseEntryAsync(entry))
                        throw Unavailable(format);
                    continue;
                }

                var token = requestToken;
                // Authorization may have changed while this open waited for another operation.
                if (!opening)
                    await EnsureAccessAsync(entry, invocation, DocumentAccess.Read, token);

                entry.ExpiresAt = invocation.ExpiresAt;
                entry.Transport.Invocation = invocation.Bind(entry);
                if (opening)
                {
                    var bytes = Math.Min(options.MaxWorkbookBytes, int.MaxValue);
                    entry.Document = await CellBridgeDocumentSession.OpenAsync(entry.Transport, new Uri(document.CanonicalUrl),
                        new CellBridgeDocumentSessionOptions
                        {
                            Mode = CellBridgeDocumentSessionMode.LazyWriter,
                            AutomaticallyRenewParticipation = false,
                            LeaseTimeoutSeconds = _leaseTimeoutSeconds,
                            MaxPackageBytes = checked((int)bytes),
                            MaxGraphBytes = bytes * 4,
                        }, token);
                    entry.Initialize(bytes, options.MaxReadCells);
                }
                else
                {
                    await entry.Document!.RenewParticipationAsync(token);
                }

                var revision = entry.Revision;
                token.ThrowIfCancellationRequested();
                entry.LastUsed = clock.GetUtcNow();
                return new(entry.Handle, key.ResourceId, entry.TokenFor(revision));
            }
            catch
            {
                if (opening)
                    await CloseEntryAsync(entry);
                throw;
            }
            finally
            {
                entry.Transport.Invocation = null;
                entry.Gate.Release();
            }
        }
    }

    public async Task<T> UseAsync<T>(McpRequestIdentity identity, string handle, McpDocumentFormat format, DocumentAccess access,
        Func<McpWorkbookEntry, CancellationToken, Task<T>> use, CancellationToken cancellationToken,
        bool closing = false)
    {
        var invocation = ResolveInvocation(identity);
        McpWorkbookEntry entry;
        lock (_gate)
        {
            ThrowIfStopping();
            if (handle is not { Length: HandleBytes * 2 } || !_handles.TryGetValue(handle, out entry!) ||
                entry.Format != format || entry.Key.SessionId != invocation.SessionId ||
                entry.Key.Subject != invocation.Actor.Identity.Subject)
                throw Unavailable(format);
        }

        // An expired session can still be closed promptly and never reaches a tool callback.
        await WaitForRequestAsync(entry, cancellationToken);
        try
        {
            if (entry.Closing || entry.Removed || (entry.Closed && !closing))
                throw Unavailable(format);
            if (entry.IsExpired(clock.GetUtcNow()) || invocation.IsExpired)
            {
                await CloseEntryAsync(entry);
                throw Unavailable(format);
            }

            using var deadline = invocation.CreateDeadline();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline?.Token ?? default);
            var token = lifetime.Token;
            await EnsureAccessAsync(entry, invocation, access, token);
            entry.ExpiresAt = invocation.ExpiresAt;
            entry.Transport.Invocation = invocation.Bind(entry);
            if (closing)
            {
                if (!await CloseEntryAsync(entry))
                    throw new McpException(format switch
                    {
                        McpDocumentFormat.Excel => "Workbook participation release could not be confirmed. Retry close_workbook.",
                        McpDocumentFormat.Word => "Document participation release could not be confirmed. Retry close_word_document.",
                        _ => "Presentation participation release could not be confirmed. Retry close_presentation.",
                    });
            }
            else
                await entry.Document!.RenewParticipationAsync(token);

            var result = await use(entry, token);
            token.ThrowIfCancellationRequested();
            entry.LastUsed = clock.GetUtcNow();
            return result;
        }
        finally
        {
            entry.Transport.Invocation = null;
            entry.Gate.Release();
        }
    }

    public async Task EvictMcpSessionAsync(string sessionId)
    {
        McpWorkbookEntry[] entries;
        lock (_gate)
        {
            if (_stopping)
                return;
            entries = _workbooks.Values.Where(entry => entry.Key.SessionId == sessionId).ToArray();
            foreach (var entry in entries)
                entry.Closing = true;
        }
        await CloseEntriesWhenIdleAsync(entries);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(options.IdleSessionTimeout.TotalMilliseconds / 4, 50, 1000));
        using var timer = new PeriodicTimer(interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            McpWorkbookEntry[] entries;
            lock (_gate)
                entries = _handles.Values.ToArray();
            foreach (var entry in entries)
                await MaintainEntryAsync(entry, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        McpWorkbookEntry[] entries;
        lock (_gate)
        {
            _stopping = true;
            entries = _handles.Values.ToArray();
            foreach (var entry in entries)
                entry.Closing = true;
        }
        try
        {
            await base.StopAsync(cancellationToken);
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = protocolOptions.MaxConcurrentRequests,
                CancellationToken = cancellationToken,
            };
            while (entries.Length > 0)
            {
                await Parallel.ForEachAsync(entries.GroupBy(entry => entry.Key.Subject), parallel,
                    (group, token) => CloseEntriesWhenIdleAsync(group, token));
                entries = entries.Where(entry => !entry.Removed).ToArray();
                if (entries.Length > 0)
                    await Task.Delay(ReleaseRetryDelay, clock, cancellationToken);
            }
        }
        finally
        {
            int pending;
            lock (_gate)
                pending = _handles.Count;
            if (pending > 0)
                logger.LogWarning("Shutdown did not confirm participation release for {DocumentCount} MCP documents.", pending);
        }
    }

    private Task WaitForRequestAsync(McpWorkbookEntry entry, CancellationToken cancellationToken) =>
        entry.WaitForRequestAsync(protocolOptions.MaxQueuedRequestsPerSubject, protocolOptions.RequestTimeout,
            cancellationToken);

    private async Task EnsureAccessAsync(McpWorkbookEntry entry, McpWorkbookInvocation invocation,
        DocumentAccess access, CancellationToken cancellationToken)
    {
        var target = new DocumentIdentity.Resource(entry.Key.ResourceId);
        if ((await ResolveAsync(access))?.LifecycleGeneration == entry.LifecycleGeneration)
            return;

        // Losing only write access does not invalidate a workbook the actor can still read.
        if (access == DocumentAccess.Read ||
            (await ResolveAsync(DocumentAccess.Read))?.LifecycleGeneration != entry.LifecycleGeneration)
            await CloseEntryAsync(entry);
        throw Unavailable(entry.Format);

        async Task<CellBridgeResolvedDocument?> ResolveAsync(DocumentAccess required)
        {
            try
            {
                return await host.ResolveDocumentAsync(target, invocation.Actor, invocation.PublicOrigin,
                    invocation.PathBase, required, cancellationToken);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Forbidden)
            {
                return null;
            }
        }
    }

    private McpWorkbookInvocation ResolveInvocation(McpRequestIdentity identity)
    {
        var actor = CellBridgeActor.FromPrincipal(identity.Context.User);
        if (actor is null || actor.Identity.Subject != identity.TransportSubject)
            throw Unavailable();
        var origin = CellBridgeInProcessHost.ResolvePublicOrigin(identity.Context) ??
            throw new McpException("The CellBridge public origin is unavailable.");
        var invocation = new McpWorkbookInvocation(actor with { CanCreate = false }, identity.SessionId, identity.ExpiresAt,
            origin, identity.Context.Request.PathBase.Value ?? "", clock);
        if (invocation.IsExpired)
            throw Unavailable();
        return invocation;
    }

    private async Task MaintainEntryAsync(McpWorkbookEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!entry.Gate.Wait(0))
            return;
        try
        {
            var now = clock.GetUtcNow();
            if (entry.Closed ? entry.RetryReleaseAt <= now : entry.IsExpired(now))
                await CloseEntryAsync(entry, cancellationToken);
        }
        finally { entry.Gate.Release(); }
    }

    private async ValueTask CloseEntriesWhenIdleAsync(IEnumerable<McpWorkbookEntry> entries,
        CancellationToken cancellationToken = default)
    {
        foreach (var entry in entries)
        {
            await entry.Gate.WaitAsync(cancellationToken);
            try { await CloseEntryAsync(entry, cancellationToken); }
            finally { entry.Gate.Release(); }
        }
    }

    private async Task<bool> CloseEntryAsync(McpWorkbookEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.Removed)
            return true;
        if (!entry.Closed)
        {
            entry.Closed = true;
            var document = entry.Document;
            entry.ClearDocument();
            try
            {
                if (document is not null)
                    await document.DisposeAsync();
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Failed to dispose a CellBridge MCP document session.");
            }
        }

        bool confirmed;
        try
        {
            await entry.Transport.ReleaseAsync(cancellationToken);
            confirmed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Failed to release CellBridge MCP document participation.");
            if (!entry.Transport.LeaseExpired)
            {
                // Keep the session slot until cleanup can retry.
                entry.RetryReleaseAt = clock.GetUtcNow() + ReleaseRetryDelay;
                return false;
            }
            confirmed = false;
        }

        lock (_gate)
        {
            entry.Removed = true;
            _workbooks.Remove(entry.Key);
            _handles.Remove(entry.Handle);
        }
        return confirmed;
    }

    private void ThrowIfStopping()
    {
        if (_stopping)
            throw new McpException("The workbook registry is shutting down.");
    }

    public static McpException Unavailable(McpDocumentFormat format = McpDocumentFormat.Excel) => new(format switch
    {
        McpDocumentFormat.Excel => "Workbook unavailable.",
        McpDocumentFormat.Word => "Word document unavailable.",
        _ => "Presentation unavailable.",
    });

    private static bool MatchesPath(string path, McpDocumentFormat format)
    {
        return format switch
        {
            McpDocumentFormat.Word => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase),
            McpDocumentFormat.PowerPoint => Path.GetExtension(path).Equals(".pptx", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    internal sealed record WorkbookKey(string Subject, string SessionId, Guid ResourceId);
}

internal enum McpDocumentFormat { Excel, Word, PowerPoint }
internal sealed record McpOpenDocument(string Handle, Guid ResourceId, string Revision);
