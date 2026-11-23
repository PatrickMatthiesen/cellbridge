using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace CellBridge.Mcp.AspNetCore;

internal sealed class McpTransportSessions(CellBridgeMcpOptions options, McpWorkbookRegistry workbooks)
{
    public const string Header = "Mcp-Session-Id";
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _actorCounts = new(StringComparer.Ordinal);
    private int _total;

    public PendingSession? Reserve(string subject)
    {
        lock (_gate)
        {
            var count = _actorCounts.GetValueOrDefault(subject);
            if (_total >= options.MaxTotalSessions || count >= options.MaxConcurrentSessionsPerActor)
                return null;

            _actorCounts[subject] = count + 1;
            _total++;
            return new PendingSession(this, subject);
        }
    }

    public bool IsOwnedBy(string sessionId, string subject)
    {
        lock (_gate)
            return _owners.TryGetValue(sessionId, out var owner) && owner == subject;
    }

    public Task RunSession(HttpContext context, McpServer server, CancellationToken cancellationToken)
    {
        var reservation = context.Features.Get<PendingSession>()
            ?? throw new InvalidOperationException("The MCP endpoint did not reserve a session.");
        var sessionId = server.SessionId
            ?? throw new InvalidOperationException("The MCP transport did not allocate a session ID.");
        lock (_gate)
        {
            _owners.Add(sessionId, reservation.Subject);
            reservation.Bound = true;
        }

        // The SDK flows the execution context of each POST to its own message handlers.
        // Keep the server run loop independent of the initialization request.
        using (ExecutionContext.SuppressFlow())
            return Task.Run(() => RunAsync(server, sessionId, reservation.Subject, cancellationToken));
    }

    private async Task RunAsync(McpServer server, string sessionId, string subject, CancellationToken cancellationToken)
    {
        try
        {
            await server.RunAsync(cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _owners.Remove(sessionId);
                Release(subject);
            }
            await workbooks.EvictMcpSessionAsync(sessionId);
        }
    }

    private void Release(string subject)
    {
        _total--;
        var remaining = _actorCounts[subject] - 1;
        if (remaining == 0) _actorCounts.Remove(subject);
        else _actorCounts[subject] = remaining;
    }

    internal sealed class PendingSession(McpTransportSessions owner, string subject) : IDisposable
    {
        public string Subject { get; } = subject;
        public bool Bound { get; set; }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (!Bound) owner.Release(Subject);
            }
        }
    }
}
