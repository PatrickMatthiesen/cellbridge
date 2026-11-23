using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using CellBridge.Client;

namespace CellBridge.AspNetCore;

internal sealed class InProcessOfficeRealTimeConnector(CellBridgeInProcessHost host, CellBridgeActor actor)
    : ICellBridgeRealTimeConnector
{
    private const string EndpointSuffix = "/_cellbridge/rtc/";
    private readonly string subject = actor.Identity.Subject;
    private readonly DocumentAccessLimit? accessLimit = actor.AccessLimit;

    public async Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!host.TryGetRealTimeServices(out var access, out var sessions) ||
            !TryReadTicket(target, access, out var ticket)) return null;

        var channel = new InProcessChannel(this, target.Endpoint, access, sessions, ticket);
        bool connected = false;
        try
        {
            var admitted = await sessions.ConnectAsync(ticket, channel.Connection, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            connected = admitted && !channel.Connection.ConnectionAborted.IsCancellationRequested;
            return connected ? channel : null;
        }
        catch (Exception error) when (error is OfficeRealTimeBusyException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (!connected) await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool TryReadTicket(RealTimeConnectionTarget target, OfficeRealTimeAccess access,
        [NotNullWhen(true)] out OfficeRealTimeTicket? ticket)
    {
        ticket = null;
        if (target.Endpoint is not { IsAbsoluteUri: true } || target.ClientId is not { Length: <= 38 } ||
            !Guid.TryParse(target.ClientId, out var clientId) || clientId == Guid.Empty)
            return false;
        var endpoint = target.Endpoint.OriginalString;
        if (endpoint.Length > 4096 + EndpointSuffix.Length || !endpoint.EndsWith(EndpointSuffix, StringComparison.Ordinal) ||
            target.Endpoint.Query.Length != 0 || target.Endpoint.Fragment.Length != 0) return false;

        var publicBase = endpoint[..^EndpointSuffix.Length];
        var delimiter = publicBase.IndexOf("://", StringComparison.Ordinal);
        if (delimiter < 0) return false;
        var pathStart = publicBase.IndexOf('/', delimiter + 3);
        var origin = pathStart < 0 ? publicBase : publicBase[..pathStart];
        if (!host.TryResolveOrigin(origin, out var canonicalOrigin) ||
            !StringComparer.Ordinal.Equals(origin, canonicalOrigin)) return false;
        if (pathStart >= 0 &&
            (!DocumentUrlPaths.TryDecode(publicBase[pathStart..], out var mount) ||
                mount.IndexOfAny(['?', '#']) >= 0 ||
                !StringComparer.Ordinal.Equals(DocumentUrlPaths.ValidateMount(mount), publicBase[pathStart..])))
            return false;

        if (!access.TryReadRenewalTicket(publicBase, target.DocumentAccessToken, clientId, out var parsed) ||
            parsed is null || !StringComparer.Ordinal.Equals(parsed.Actor.Identity.Subject, subject) ||
            parsed.Actor.AccessLimit?.ResourceId != accessLimit?.ResourceId ||
            parsed.Actor.AccessLimit?.LifecycleGeneration != accessLimit?.LifecycleGeneration ||
            parsed.Actor.AccessLimit?.Access != accessLimit?.Access)
            return false;
        ticket = parsed;
        return true;
    }

    private sealed class InProcessChannel : ICellBridgeRealTimeChannel
    {
        private const string Hub = "RealtimeChannelHub";
        private readonly InProcessOfficeRealTimeConnector connector;
        private readonly Uri endpoint;
        private readonly OfficeRealTimeAccess access;
        private readonly IOfficeRealTimeSessionStore sessions;
        private readonly CancellationTokenSource stopping = new();
        private readonly SemaphoreSlim renewal = new(1);
        private readonly Lock closeLock = new();
        private readonly Lock lifetimeLock = new();
        private readonly Channel<string> notifications = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        private OfficeRealTimeTicket ticket;
        private Task? close;
        private bool aborted;

        internal InProcessChannel(InProcessOfficeRealTimeConnector connector, Uri endpoint, OfficeRealTimeAccess access,
            IOfficeRealTimeSessionStore sessions, OfficeRealTimeTicket ticket)
        {
            this.connector = connector;
            this.endpoint = endpoint;
            this.access = access;
            this.sessions = sessions;
            this.ticket = ticket;
            Connection = new(Guid.NewGuid().ToString("N"), stopping.Token, ReceiveAsync, Abort);
        }

        internal OfficeRealTimeConnection Connection { get; }

        public IAsyncEnumerable<string> ReadNotificationsAsync(CancellationToken cancellationToken = default) =>
            notifications.Reader.ReadAllAsync(cancellationToken);

        public async Task UpdateClientInfoAsync(RealTimeConnectionTarget target, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            await renewal.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Connection.ConnectionAborted.IsCancellationRequested)
                    throw new IOException("The CellBridge real-time connection closed.");
                if (target.Endpoint != endpoint || !connector.TryReadTicket(target, access, out var renewed) ||
                    renewed.ClientId != ticket.ClientId)
                    throw new IOException("The CellBridge real-time ticket renewal was rejected.");

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Connection.ConnectionAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                if (!await sessions.RenewTicketAsync(ticket, renewed, Connection.ConnectionId, deadline.Token).ConfigureAwait(false))
                    throw new IOException("The CellBridge real-time ticket renewal was rejected.");
                ticket = renewed;
            }
            catch (Exception error) when (error is OfficeRealTimeBusyException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The CellBridge real-time ticket renewal was rejected.", error);
            }
            finally { renewal.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (closeLock)
                return new(close ??= CloseAsync());
        }

        private Task ReceiveAsync(string method, object?[] arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Connection.ConnectionAborted.IsCancellationRequested)
                notifications.Writer.TryWrite(JsonSerializer.Serialize(new { H = Hub, M = method, A = arguments }));
            return Task.CompletedTask;
        }

        private void Abort()
        {
            lock (lifetimeLock)
            {
                if (aborted) return;
                aborted = true;
                notifications.Writer.TryComplete();
                stopping.Cancel();
            }
        }

        private async Task CloseAsync()
        {
            Abort();
            await renewal.WaitAsync().ConfigureAwait(false);
            try
            {
                await sessions.DisconnectAsync(Connection.ConnectionId, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lock (lifetimeLock) stopping.Dispose();
                renewal.Release();
            }
        }
    }
}
