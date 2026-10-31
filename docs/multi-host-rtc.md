# Multi-host Office RTC

Office RTC is enabled by default. A single host uses local rooms and does not
resolve a Redis connection, start a distributed scheduler, or poll Redis.
Multiple hosts use shared Redis coordination and local SignalR delivery.
Document storage and file publication continue to use the configured document
provider. Redis does not store file revisions.

## Configure hosts

Use shared document state and shared content. Register each host with the same
Redis primary and an application/library namespace that no unrelated deployment
uses:

```csharp
builder.Services.AddCellBridge(provider, multipleInstances: true, configure: options =>
{
    options.OfficeRealTimeRedisConnectionString = redisConnectionString;
    options.OfficeRealTimeRedisNamespace = "customer-workspace-documents";
});
```

Missing Redis configuration fails registration. It does not disable RTC.
WebDemo accepts `Protocol:OfficeRealTimeRedisConnectionString` and
`Protocol:OfficeRealTimeRedisNamespace`, together with
`Storage:MultipleInstances=true`. Supply credentials through host configuration.
Do not put them in source control. To disable RTC explicitly, set
`EnableOfficeRealTime=false`.

Use one authoritative Redis primary with no eviction of coordination keys.
Protect Redis with network access controls, authentication, and transport
encryption appropriate to the deployment. It contains transient presence,
typing payloads, and actor identity metadata. All keys in one application/library
namespace share a Redis hash slot so room membership and subject limits can
change atomically. This limits how that namespace can use Redis Cluster capacity.
The implementation does not promise continuity across a Redis failover or reset.
The Redis identity also needs access to the namespace's wake channels for
`PUBLISH` and pattern subscriptions. Startup opens that subscription, and a
subscription failure requires existing sessions to reconnect.

Every host must share its ASP.NET DataProtection key repository, application
discriminator, and the certificate/private key that decrypts those keys.
The packaged authentication integration encrypts keys with a certificate.
Sharing only the key repository is insufficient. Hosts must also use the same
public HTTPS origin and mount path, and resolve the same immutable authorization
policy versions. Configure negotiation and transport affinity at the load balancer.
See [ASP.NET Core deployment guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane?view=aspnetcore-10.0).

RTC requires stored grants or an immutable versioned authorization policy.
Ordinary asynchronous authorization providers and legacy access evaluators remain
unsupported for RTC. The distributed dispatcher does not use an additional
automatic SignalR Redis fanout layer.

## Delivery and authorization

A room identifies one resource and document lifecycle generation. Its random
epoch distinguishes Redis state created after a reset. Each membership has a
host identity and a fresh connection incarnation. Events have increasing room
sequences. Presence snapshots return an atomic sequence watermark. Each recipient
suppresses older queued presence changes covered by its snapshot, while typing
events retain their independent delivery behavior.
Each connection also keeps its admission snapshot's watermark. It skips all
earlier events, including typing. Later presence snapshots do not advance that
admission watermark or discard typing sent after admission.

The server serializes snapshots and dispatch within each local room. SignalR can
write a newer presence callback before it writes a concurrent snapshot invocation
completion. A snapshot describes its atomic observation, not completion time.
Native Office reconciliation of those messages requires separate qualification.

Before delivery, the receiving host observes current Redis membership, then
authorizes the sender and recipient against a common current document snapshot.
It checks local recipient incarnation, ticket expiry, and broker health before
initiating the send. Remote replacement after the Redis observation can overlap
an already admitted dispatch. Redis membership and database authorization are
separate observations, not one transaction. The server cannot recall a message
that it has sent. Departure notifications authorize current recipients without
requiring the departed sender to retain access.

Delivery is transient. An accepted invocation does not acknowledge every
recipient or promise exactly-once delivery. A failed operation can have delivered
to some recipients. The server does not retry an ambiguous typing mutation.
Missing sequences, lost room state, epoch changes, broker interruption, or queue
overflow close affected local sessions. Reconnecting obtains a fresh presence
snapshot and discards the old typing backlog.

Tickets expire after 20 minutes. Renewal requires fresh protocol metadata and a
new SignalR connection. A membership heartbeat extends neither the ticket nor
the document's editor/coauthor lease. The client renews those leases through the
document protocol. Reconnection can select a different host.

## Bounds and failure handling

The configured defaults are implementation limits, not measured capacity claims:

| Item | Bound or healthy-operation target |
| --- | --- |
| Transport connections | 128 per host |
| Members | 32 per document generation across hosts |
| Subject memberships | 8 across all rooms in the namespace |
| Application payload | 64 KiB; serialized event 80 KiB |
| Host dispatch workers | 4, with one active operation per local room; fair rotating ready queue |
| Concurrent recipient sends | At most 4 per worker, 16 per host, including unfinished canceled sends |
| Event read | At most 32 events and 256 KiB |
| Local pending events | 64 events / 1 MiB per room; 256 events / 4 MiB per host |
| Room log | 256 events / 4 MiB |
| Delivery-eligible age | 30 seconds, checked against Redis time |
| Event/member pruning | At most 32 records per operation |
| Fallback poll / reap targets | 250 milliseconds / 1 second |
| Heartbeat / membership lease | 2 seconds / 10 seconds |
| Idle authorization sweep | Every 5 seconds per occupied local room |
| Operation / recipient send deadline | 5 seconds / 1 second |
| Local expiry/deadline pass | At least every 100 milliseconds, independent of I/O workers |
| Inactive room/log expiry | 120 seconds after final refresh |

Expired events can remain physically stored until bounded cleanup or inactive
key expiry. Reads do not deliver events older than 30 seconds. Cleanup makes
progress when admission or append returns busy. Count and byte bounds still
apply. Subject indexes use expiring memberships rather than crash-sensitive
counters.

The host performs no room or database polling without local connections. Idle
authorization checks batch members by local room. Missed heartbeat,
authorization, or broker-health deadlines close affected sessions. Local ticket
expiry does not wait for Redis cleanup or an available I/O worker. Host failure
leaves memberships until their leases expire; another active room worker can
remove their presence. Recovery requires fresh sessions rather than replay of
old typing. Document persistence is independent of this transient RTC state.

Redis publishes a small wake hint after every event append. A hint queues a
coalesced local log read; it carries no ticket or payload and cannot authorize a
send, advance a cursor, or renew membership. Lost hints are harmless because the
host still reads the ordered log on the fallback interval. A subscription
connection failure invalidates current sessions. The independent 50 millisecond
deadline pass continues when all dispatch workers are occupied.

Each group of at most four local recipients gets a fresh Redis membership
observation and one common authorization batch. The worker starts permitted
sends before awaiting them and monitors their deadlines independently. It keeps
the room coordinator and worker slot until all sends finish, even when a
transport ignores cancellation. PostgreSQL fetches the normalized head and
control rows in one statement per coherent read. Authorization still takes two
separate fresh control reads and retains the existing integrity checks.

Opt-in raw protocol capture retains its existing behavior. Disable it for
performance comparisons. New operational logs and metrics must not contain
tickets or payloads.

## Proxy affinity

Connection affinity keeps negotiation and subsequent transport requests on the
same host. User affinity alone does not place all collaborators on one host.
An existing WebSocket stays on its accepting host until it disconnects.

Room affinity can improve locality when an application supplies the same routing
key for a resource and lifecycle generation on every request for that room. The
current native Office discovery URLs contain protected tickets, not a public
room-routing key. A proxy cannot infer that key by decoding a ticket. This change
does not add a routing parameter, redirect, or native Office routing contract.

For a separately controlled client, an application could supply an opaque room
key on every negotiation and transport request. An explicitly configured proxy
could then hash that key. For example, NGINX supports `hash $arg_room consistent`
in an upstream. That example requires a client contract supplying `room`; it is
not a configuration that can be applied to the current Office URLs. The backend
must authenticate the ticket independently, and the proxy must also preserve
connection affinity when upstream membership changes. See the
[NGINX hash documentation](https://nginx.org/en/docs/http/ngx_http_upstream_module.html#hash)
and [SignalR hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale).

Co-located collaborators still use Redis coordination. Bypassing it would require
a separate room ownership and failover design. A global user cookie also cannot
place each of that user's open documents independently.

## Qualification

Simulated protocol and SignalR traffic can verify server authorization, room
coordination, and failure behavior. It does not qualify native Office startup,
token renewal, callback reconciliation, OCS, or end-user typing latency.
Bounded load runs apply only to their reported topology, environment, and traffic.
See [automated testing](automated-testing.md) for validation commands.
