using CellBridge.AspNetCore;
using CellBridge.Client;
using CellBridge.Client.Excel;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using System.Net;
using System.Security.Cryptography;

namespace CellBridge.Mcp.AspNetCore;

internal sealed class McpWorkbookRegistry(
    CellBridgeInProcessHost host, CellBridgeMcpOptions options, TimeProvider clock, ILogger<McpWorkbookRegistry> logger)
    : BackgroundService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, McpWorkbookEntry> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<WorkbookKey, McpWorkbookEntry> _workbooks = [];
    private bool _stopping;

    public int MaxReadCells => options.MaxReadCells;

    public async Task<McpOpenWorkbookResult> OpenAsync(McpRequestIdentity identity, DocumentIdentity target,
        CancellationToken cancellationToken)
    {
        var invocation = ResolveInvocation(identity);
        using var deadline = invocation.CreateDeadline();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline?.Token ?? default);
        var requestToken = lifetime.Token;
        while (true)
        {
            var document = await host.ResolveDocumentAsync(target, invocation.Actor, invocation.PublicOrigin,
                invocation.PathBase, DocumentAccess.Read, requestToken) ?? throw Unavailable();
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
                        throw new McpException("The open workbook session limit was reached.");
                    entry = new McpWorkbookEntry(key, document.LifecycleGeneration,
                        invocation.ExpiresAt, options.IdleSessionTimeout, clock.GetUtcNow(),
                        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                        new McpWorkbookTransport(host, key.ResourceId, document.LifecycleGeneration, key.Subject));
                    entry.Gate.Wait(0);
                    _workbooks.Add(key, entry);
                    _handles.Add(entry.Handle, entry);
                }
            }

            if (!opening)
                await entry.Gate.WaitAsync(requestToken);

            try
            {
                requestToken.ThrowIfCancellationRequested();
                if (entry.Closing)
                    throw Unavailable();
                if (entry.Closed)
                    continue;
                if (entry.LifecycleGeneration != document.LifecycleGeneration || entry.IsExpired(clock.GetUtcNow()))
                {
                    await CloseEntryAsync(entry);
                    continue;
                }

                var current = invocation.WithWorkbookExpiry(entry.ExpiresAt);
                using var entryDeadline = current.CreateDeadline();
                using var entryLifetime = CancellationTokenSource.CreateLinkedTokenSource(requestToken, entryDeadline?.Token ?? default);
                var token = entryLifetime.Token;
                // Authorization may have changed while this open waited for another operation.
                if (!opening)
                    await EnsureAccessAsync(entry, current, DocumentAccess.Read, token);

                entry.Transport.Invocation = current.Bind(entry);
                if (opening)
                {
                    var bytes = Math.Min(options.MaxWorkbookBytes, int.MaxValue);
                    entry.Document = await CellBridgeDocumentSession.OpenAsync(entry.Transport, new Uri(document.CanonicalUrl),
                        new CellBridgeDocumentSessionOptions
                        {
                            Mode = CellBridgeDocumentSessionMode.LazyWriter,
                            AutomaticallyRenewParticipation = false,
                            LeaseTimeoutSeconds = (int)Math.Clamp(Math.Ceiling(options.IdleSessionTimeout.TotalSeconds), 60, 120000),
                            MaxPackageBytes = checked((int)bytes),
                            MaxGraphBytes = bytes * 4,
                        }, token);
                    entry.Workbook = new ExcelWorkbookSession(entry.Document, new ExcelWorkbookLimits
                    {
                        MaxPackageBytes = bytes,
                        MaxSessionBytes = bytes * 8,
                        MaxCellsPerOperation = Math.Max(10_000, options.MaxReadCells),
                        TrackSyncChanges = true,
                    });
                }
                else
                {
                    await entry.Document!.RenewParticipationAsync(token);
                }

                var summary = entry.Workbook!.GetWorkbookSummary();
                token.ThrowIfCancellationRequested();
                entry.LastUsed = clock.GetUtcNow();
                return new(entry.Handle, key.ResourceId, entry.TokenFor(summary.Revision));
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

    public async Task<T> UseAsync<T>(McpRequestIdentity identity, string handle, DocumentAccess access,
        Func<McpWorkbookEntry, CancellationToken, Task<T>> use, CancellationToken cancellationToken,
        bool closing = false)
    {
        var invocation = ResolveInvocation(identity);
        McpWorkbookEntry entry;
        lock (_gate)
        {
            ThrowIfStopping();
            if (string.IsNullOrEmpty(handle) || !_handles.TryGetValue(handle, out entry!) ||
                entry.Key.SessionId != invocation.SessionId || entry.Key.Subject != invocation.Actor.Identity.Subject)
                throw Unavailable();
        }

        // An expired session can still be closed promptly and never reaches a tool callback.
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Closing)
                throw Unavailable();
            if (entry.Closed || entry.IsExpired(clock.GetUtcNow()) || invocation.IsExpired)
            {
                await CloseEntryAsync(entry);
                throw Unavailable();
            }

            var current = invocation.WithWorkbookExpiry(entry.ExpiresAt);
            using var deadline = current.CreateDeadline();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline?.Token ?? default);
            var token = lifetime.Token;
            await EnsureAccessAsync(entry, current, access, token);
            entry.Transport.Invocation = current.Bind(entry);
            if (closing)
                await CloseEntryAsync(entry);
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
                await CloseIfExpiredAsync(entry);
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
        await base.StopAsync(cancellationToken);
        await CloseEntriesWhenIdleAsync(entries).WaitAsync(cancellationToken);
    }

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
        throw Unavailable();

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

    private async Task CloseIfExpiredAsync(McpWorkbookEntry entry)
    {
        if (!entry.Gate.Wait(0))
            return;
        try
        {
            if (entry.IsExpired(clock.GetUtcNow()))
                await CloseEntryAsync(entry);
        }
        finally { entry.Gate.Release(); }
    }

    private async Task CloseEntriesWhenIdleAsync(McpWorkbookEntry[] entries)
    {
        foreach (var entry in entries)
        {
            await entry.Gate.WaitAsync();
            try { await CloseEntryAsync(entry); }
            finally { entry.Gate.Release(); }
        }
    }

    private async Task CloseEntryAsync(McpWorkbookEntry entry)
    {
        if (entry.Closed)
            return;
        entry.Closed = true;
        try
        {
            if (entry.Document is { } document)
                await document.DisposeAsync();
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Failed to dispose a CellBridge MCP workbook session.");
        }
        finally
        {
            try { await entry.Transport.ReleaseAsync(); }
            catch (Exception error) { logger.LogWarning(error, "Failed to release CellBridge MCP workbook participation."); }
            lock (_gate)
            {
                _workbooks.Remove(entry.Key);
                _handles.Remove(entry.Handle);
            }
        }
    }

    private void ThrowIfStopping()
    {
        if (_stopping)
            throw new McpException("The workbook registry is shutting down.");
    }

    public static McpException Unavailable() => new("Workbook unavailable.");

    internal sealed record WorkbookKey(string Subject, string SessionId, Guid ResourceId);
}
