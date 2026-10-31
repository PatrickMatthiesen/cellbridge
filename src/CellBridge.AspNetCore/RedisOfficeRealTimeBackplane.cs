using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CellBridge.AspNetCore;

/// <summary>Redis-backed transient room state for explicitly multi-instance hosts.</summary>
internal sealed class RedisOfficeRealTimeBackplane : IOfficeRealTimeBackplane, IOfficeRealTimeBackplaneWakeSource
{
    private const int MaxRoomMembers = 32;
    private const int MaxSubjectMembers = 8;
    private const int MaxPrune = 32;
    private const int MaxEvents = 256;
    private const int MaxEventBytes = 4 * 1024 * 1024;
    private const long EligibleEventAgeMs = 30_000;
    private const long RoomTtlMs = 120_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IConnectionMultiplexer _connection;
    private readonly IDatabase _database;
    private readonly string _prefix;
    private readonly string _hostId;
    private readonly ILogger<RedisOfficeRealTimeBackplane>? _logger;
    private long _generation;
    private long _scriptEvaluationCount;
    private long _scriptArgumentBytes;
    private long _wakeHintsReceived;
    private long _wakeHintBytesReceived;
    private int _subscriptionHealthy = 1;
    private readonly RedisChannel _wakePattern;
    private int _healthy = 1;
    private int _disposed;

    public RedisOfficeRealTimeBackplane(CellBridgeOptions options,
        ILogger<RedisOfficeRealTimeBackplane>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.OfficeRealTimeRedisConnectionString))
            throw new InvalidOperationException("Office RTC requires OfficeRealTimeRedisConnectionString when multipleInstances is enabled.");
        if (string.IsNullOrWhiteSpace(options.OfficeRealTimeRedisNamespace) || options.OfficeRealTimeRedisNamespace.Length > 256)
            throw new InvalidOperationException("Office RTC requires a stable OfficeRealTimeRedisNamespace of at most 256 characters.");

        var configuration = ConfigurationOptions.Parse(options.OfficeRealTimeRedisConnectionString);
        if (configuration.EndPoints.Count != 1)
            throw new NotSupportedException("Office RTC supports one authoritative Redis primary endpoint.");
        configuration.AbortOnConnectFail = true;
        configuration.ConnectRetry = Math.Clamp(configuration.ConnectRetry, 1, 3);
        configuration.AsyncTimeout = Math.Min(configuration.AsyncTimeout, 5_000);
        _connection = ConnectionMultiplexer.Connect(configuration);
        if (!_connection.IsConnected) throw new InvalidOperationException("Office RTC Redis is unavailable during startup.");
        _database = _connection.GetDatabase();
        string hashTag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.OfficeRealTimeRedisNamespace)))[..24];
        _prefix = $"cellbridge:rtc:{{{hashTag}}}:";
        _hostId = Guid.NewGuid().ToString("N");
        _logger = logger;
        _connection.ConnectionFailed += OnConnectionFailed;
        _connection.ConnectionRestored += OnConnectionRestored;
        _wakePattern = RedisChannel.Pattern(_prefix + "room:*:meta:wake");
        try { _connection.GetSubscriber().Subscribe(_wakePattern, OnWakeHint); }
        catch { _connection.Dispose(); throw; }
    }

    public long Generation => Interlocked.Read(ref _generation);
    public bool IsHealthy => Volatile.Read(ref _healthy) == 1 && Volatile.Read(ref _subscriptionHealthy) == 1 && _connection.IsConnected && Volatile.Read(ref _disposed) == 0;
    internal long ScriptEvaluationCount => Interlocked.Read(ref _scriptEvaluationCount);
    internal long ScriptArgumentBytes => Interlocked.Read(ref _scriptArgumentBytes);
    internal string HostId => _hostId;
    internal long WakeHintsReceived => Interlocked.Read(ref _wakeHintsReceived);
    internal long WakeHintBytesReceived => Interlocked.Read(ref _wakeHintBytesReceived);
    public event Action<OfficeRealTimeRoomKey>? RoomChanged;

    private void OnWakeHint(RedisChannel channel, RedisValue value)
    {
        if (Volatile.Read(ref _disposed) != 0 || value != "1") return;
        string text = channel.ToString();
        string prefix = _prefix + "room:";
        const string suffix = ":meta:wake";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal)) return;
        string identity = text[prefix.Length..^suffix.Length];
        int separator = identity.IndexOf(':');
        if (separator != 32 || !Guid.TryParseExact(identity[..separator], "N", out var resource) ||
            !long.TryParse(identity[(separator + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long generation) || generation < 1) return;
        var room = new OfficeRealTimeRoomKey(resource, generation);
        if (text != RoomKey(room) + suffix) return;
        Interlocked.Increment(ref _wakeHintsReceived);
        Interlocked.Add(ref _wakeHintBytesReceived, Encoding.UTF8.GetByteCount(text) + 1);
        // This callback only signals bounded local scheduling state. The log,
        // membership observation and authorization still decide delivery.
        RoomChanged?.Invoke(room);
    }

    public async Task<OfficeRealTimeBackplaneConnectResult> ConnectAsync(OfficeRealTimeBackplaneMember member,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.Incarnation == Guid.Empty || member.Ticket.ResourceId != member.Room.ResourceId ||
            member.Ticket.LifecycleGeneration != member.Room.LifecycleGeneration) return new(OfficeRealTimeBackplaneStatus.Denied, null);
        var roomKeys = RoomKeys(member.Room);
        string subject = SubjectKey(member.Ticket.Actor.Identity.Subject);
        member = Enrich(member);
        RedisResult result = await EvalAsync(ConnectScript, [.. roomKeys, subject],
            [Serialize(member), Guid.NewGuid().ToString("N"), MaxRoomMembers, MaxSubjectMembers,
                MaxPrune, RoomTtlMs, 10_000, MaxEvents, MaxEventBytes, EligibleEventAgeMs], cancellationToken).ConfigureAwait(false);
        return ParseConnect(result);
    }

    public async Task<OfficeRealTimeBackplaneSnapshotResult> ReadSnapshotAsync(OfficeRealTimeRoomKey room,
        CancellationToken cancellationToken)
    {
        RedisResult result = await EvalAsync(SnapshotScript, RoomKeys(room),
            [MaxPrune, RoomTtlMs, MaxEvents, MaxEventBytes, EligibleEventAgeMs], cancellationToken).ConfigureAwait(false);
        return ParseSnapshot(result);
    }

    public async Task<OfficeRealTimeBackplaneEventsResult> ReadEventsAsync(OfficeRealTimeRoomKey room,
        string epoch, long afterSequence, int maxEvents, int maxBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(epoch) || afterSequence < 0 || maxEvents is < 1 or > 32 || maxBytes is < 80 * 1024 or > 256 * 1024)
            return new(OfficeRealTimeBackplaneStatus.Denied, epoch, afterSequence, 0, [], false, false);
        RedisResult result = await EvalAsync(ReadEventsScript, RoomKeys(room),
            [epoch, afterSequence, maxEvents, maxBytes, MaxPrune, MaxEvents, MaxEventBytes,
                EligibleEventAgeMs, RoomTtlMs], cancellationToken).ConfigureAwait(false);
        return ParseEvents(result);
    }

    public async Task<OfficeRealTimeBackplaneMembersResult> ObserveMembersAsync(OfficeRealTimeRoomKey room,
        CancellationToken cancellationToken)
    {
        RedisResult result = await EvalAsync(ObserveMembersScript, RoomKeys(room),
            [MaxPrune, RoomTtlMs], cancellationToken).ConfigureAwait(false);
        return ParseMembers(result);
    }

    public async Task<OfficeRealTimeBackplaneMutationResult> MutateAsync(OfficeRealTimeBackplaneMember member,
        string epoch, string eventId, string kind, string clientKey, string method, string payloadJson,
        CancellationToken cancellationToken)
    {
        if (kind is not ("presence" or "notification") || string.IsNullOrEmpty(eventId) || eventId.Length > 64 ||
            payloadJson.Length > 80 * 1024 || Encoding.UTF8.GetByteCount(payloadJson) > 80 * 1024)
            return new(OfficeRealTimeBackplaneStatus.Denied, epoch, 0, 0);
        string eventTemplate = JsonSerializer.Serialize(new
        {
            eventId, epoch, createdUtc = DateTimeOffset.UtcNow, redisTimeMilliseconds = 0L, sequence = 0L,
            kind, clientKey, method, payloadJson,
        }, Json);
        RedisResult result = await EvalAsync(MutateScript, RoomKeys(member.Room),
            [epoch, member.Incarnation.ToString("D").ToLowerInvariant(), member.ConnectionId, eventId, kind, clientKey,
                eventTemplate, MaxPrune, MaxEvents, MaxEventBytes,
                EligibleEventAgeMs, RoomTtlMs], cancellationToken).ConfigureAwait(false);
        return ParseMutation(result);
    }

    public async Task<OfficeRealTimeBackplaneHeartbeatResult> HeartbeatAsync(
        IReadOnlyList<OfficeRealTimeBackplaneMember> members, CancellationToken cancellationToken)
    {
        if (members.Count is < 1 or > MaxRoomMembers || members.Select(m => m.Room).Distinct().Count() != 1)
            return new(OfficeRealTimeBackplaneStatus.Denied, new HashSet<Guid>(), 0);
        RedisResult result = await EvalAsync(HeartbeatScript, RoomKeys(members[0].Room),
            [JsonSerializer.Serialize(members.Select(m => m.Incarnation.ToString("D").ToLowerInvariant()).ToArray(), Json),
                10_000, RoomTtlMs], cancellationToken).ConfigureAwait(false);
        return ParseHeartbeat(result);
    }

    public async Task<OfficeRealTimeBackplaneRemoveResult> RemoveAsync(OfficeRealTimeBackplaneMember member,
        string? presenceClientKey, string eventId, CancellationToken cancellationToken)
    {
        string eventTemplate = JsonSerializer.Serialize(new
        {
            eventId, epoch = "", createdUtc = DateTimeOffset.UtcNow, redisTimeMilliseconds = 0L, sequence = 0L,
            kind = "departure", clientKey = presenceClientKey ?? "",
            method = "presenceUpdated", payloadJson = "{}",
        }, Json);
        RedisResult result = await EvalAsync(RemoveScript, RoomKeys(member.Room),
            [member.Incarnation.ToString("D").ToLowerInvariant(), member.ConnectionId, presenceClientKey ?? "", eventId,
                eventTemplate, MaxPrune, MaxEvents, MaxEventBytes, EligibleEventAgeMs, RoomTtlMs], cancellationToken).ConfigureAwait(false);
        return ParseRemove(result);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _connection.ConnectionFailed -= OnConnectionFailed;
        _connection.ConnectionRestored -= OnConnectionRestored;
        RoomChanged = null;
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<RedisResult> EvalAsync(string script, RedisKey[] keys, object[] args,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        Task<RedisResult>? pending = null;
        try
        {
            var values = new RedisValue[args.Length];
            for (int i = 0; i < args.Length; i++) values[i] = args[i] switch
            {
                string text => text,
                int number => number,
                long number => number,
                _ => JsonSerializer.Serialize(args[i], Json),
            };
            Interlocked.Increment(ref _scriptEvaluationCount);
            long argumentBytes = Encoding.UTF8.GetByteCount(script);
            foreach (RedisKey key in keys) argumentBytes += Encoding.UTF8.GetByteCount(key.ToString());
            foreach (RedisValue value in values) argumentBytes += value.IsNull ? 0 : Encoding.UTF8.GetByteCount(value.ToString());
            Interlocked.Add(ref _scriptArgumentBytes, argumentBytes);
            pending = _database.ScriptEvaluateAsync(script, keys, values, CommandFlags.DemandMaster);
            RedisResult result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_connection.IsConnected) Volatile.Write(ref _healthy, 1);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A dispatched mutation may have committed despite caller cancellation. Mark
            // the broker generation unhealthy; session code must invalidate and reconnect.
            MarkReset("A Redis operation ended with an unknown outcome.");
            if (pending is not null)
            {
                try { await pending.ConfigureAwait(false); }
                catch { /* Keep scheduler accounting until the dispatched command settles. */ }
            }
            throw;
        }
        catch (RedisException error)
        {
            MarkReset("Redis coordination failed.");
            throw new InvalidOperationException("Office RTC Redis coordination failed; the active room must reconnect.", error);
        }
    }

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs args)
    {
        if (args.ConnectionType == ConnectionType.Subscription) Volatile.Write(ref _subscriptionHealthy, 0);
        MarkReset("Redis connection failed.");
    }
    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs args)
    {
        if (args.ConnectionType == ConnectionType.Subscription) Volatile.Write(ref _subscriptionHealthy, 1);
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _healthy, 1);
        _logger?.LogWarning("Office RTC Redis was restored; active sessions must reconnect.");
    }
    private void MarkReset(string reason)
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _healthy, 0);
        _logger?.LogWarning("Office RTC backplane state requires reconnect. Reason: {Reason}", reason);
    }

    private string RoomKey(OfficeRealTimeRoomKey room) => $"{_prefix}room:{room.ResourceId:N}:{room.LifecycleGeneration}";
    private RedisKey[] RoomKeys(OfficeRealTimeRoomKey room)
    {
        string key = RoomKey(room);
        return [$"{key}:meta", $"{key}:members", $"{key}:expiry", $"{key}:presence", $"{key}:events"];
    }
    private string SubjectKey(string subject) => $"{_prefix}subject:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)))}";
    private OfficeRealTimeBackplaneMember Enrich(OfficeRealTimeBackplaneMember member) => member with
    {
        ClientKey = member.Ticket.ClientId.ToString("B").ToLowerInvariant(),
        SubjectIndexKey = SubjectKey(member.Ticket.Actor.Identity.Subject),
        SubjectIndexMember = IndexMember(member),
    };
    private static string IndexMember(OfficeRealTimeBackplaneMember member) =>
        $"{member.Room.ResourceId:N}:{member.Room.LifecycleGeneration}:{member.ConnectionId}:{member.Incarnation:N}";
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static OfficeRealTimeBackplaneConnectResult ParseConnect(RedisResult result)
    {
        var array = (RedisResult[])result!;
        var status = ParseStatus(array[0].ToString());
        return new(status, status == OfficeRealTimeBackplaneStatus.Ok ? ParseSnapshotJson(array[1].ToString()) : null);
    }

    private static OfficeRealTimeBackplaneSnapshotResult ParseSnapshot(RedisResult result)
    {
        var array = (RedisResult[])result!;
        var status = ParseStatus(array[0].ToString());
        return new(status, status == OfficeRealTimeBackplaneStatus.Ok ? ParseSnapshotJson(array[1].ToString()) : null);
    }

    private static OfficeRealTimeBackplaneSnapshot ParseSnapshotJson(string json) =>
        JsonSerializer.Deserialize<OfficeRealTimeBackplaneSnapshot>(json, Json)!;

    private static OfficeRealTimeBackplaneEventsResult ParseEvents(RedisResult result)
    {
        var array = (RedisResult[])result!;
        var status = ParseStatus(array[0].ToString());
        return new(status, NullIfEmpty(array[1].ToString()), Number(array[2]), Number(array[3]),
            status == OfficeRealTimeBackplaneStatus.Ok
                ? ParseEncodedEvents(array[4].ToString()) : [],
            array[5].ToString() == "1", array[6].ToString() == "1");
    }

    private static OfficeRealTimeBackplaneEvent[] ParseEncodedEvents(string json)
    {
        // ReadEventsScript concatenates the stored records without re-encoding.
        // GetRawText preserves each record's spelling and escaping, so this
        // count matches Lua's #raw and the room log's byte accounting.
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(element =>
            element.Deserialize<OfficeRealTimeBackplaneEvent>(Json)!
                with { EncodedBytes = Encoding.UTF8.GetByteCount(element.GetRawText()) }).ToArray();
    }

    private static OfficeRealTimeBackplaneMembersResult ParseMembers(RedisResult result)
    {
        var array = (RedisResult[])result!;
        var status = ParseStatus(array[0].ToString());
        return new(status, NullIfEmpty(array[1].ToString()),
            status == OfficeRealTimeBackplaneStatus.Ok
                ? JsonSerializer.Deserialize<OfficeRealTimeBackplaneMember[]>(array[2].ToString(), Json) ?? [] : [], Number(array[3]));
    }

    private static OfficeRealTimeBackplaneMutationResult ParseMutation(RedisResult result)
    {
        var array = (RedisResult[])result!;
        return new(ParseStatus(array[0].ToString()), NullIfEmpty(array[1].ToString()), Number(array[2]), Number(array[3]));
    }

    private static OfficeRealTimeBackplaneHeartbeatResult ParseHeartbeat(RedisResult result)
    {
        var array = (RedisResult[])result!;
        return new(ParseStatus(array[0].ToString()),
            (JsonSerializer.Deserialize<string[]>(array[1].ToString(), Json) ?? []).Select(Guid.Parse).ToHashSet(), Number(array[2]));
    }

    private static OfficeRealTimeBackplaneRemoveResult ParseRemove(RedisResult result)
    {
        var array = (RedisResult[])result!;
        var seq = Number(array[2]);
        return new(ParseStatus(array[0].ToString()), NullIfEmpty(array[1].ToString()), seq == 0 ? null : seq, Number(array[3]));
    }

    private static OfficeRealTimeBackplaneStatus ParseStatus(string value) => value switch
    {
        "ok" => OfficeRealTimeBackplaneStatus.Ok,
        "busy" => OfficeRealTimeBackplaneStatus.Busy,
        "denied" => OfficeRealTimeBackplaneStatus.Denied,
        "missing" => OfficeRealTimeBackplaneStatus.Missing,
        "reset" => OfficeRealTimeBackplaneStatus.Reset,
        _ => throw new InvalidDataException("Redis returned an unknown RTC coordination status."),
    };
    private static long Number(RedisResult value) => long.TryParse(value.ToString(), out long parsed) ? parsed : 0;
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private const string ConnectScript = """
        local meta=KEYS[1]; local members=KEYS[2]; local expiry=KEYS[3]; local presence=KEYS[4]; local events=KEYS[5]; local subject=KEYS[6]
        local eventPruned=0
        local function clock() local t=redis.call('TIME'); return tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000) end
        local function touch(ttl) for i=1,5 do redis.call('PEXPIRE',KEYS[i],ttl) end end
        local function append(e,now,maxCount,maxBytes,eligible,prune)
          local bytes=tonumber(redis.call('HGET',meta,'bytes') or '0')
          local seq=tonumber(redis.call('HGET',meta,'seq') or '0')+1; e.sequence=seq; e.epoch=redis.call('HGET',meta,'epoch'); e.redisTimeMilliseconds=now
          local encoded=cjson.encode(e)
          while eventPruned<prune do local raw=redis.call('LINDEX',events,0); if not raw then break end; local old=cjson.decode(raw)
            local mustTrim=now-tonumber(old.redisTimeMilliseconds or 0)>eligible or redis.call('LLEN',events)>=maxCount or bytes+#encoded>maxBytes
            if not mustTrim then break end
            redis.call('LPOP',events); bytes=bytes-#raw; eventPruned=eventPruned+1
          end
          redis.call('HSET',meta,'bytes',bytes)
          local raw=redis.call('LINDEX',events,0)
          if raw and now-tonumber(cjson.decode(raw).redisTimeMilliseconds or 0)>eligible then return false end
          e.sequence=seq; e.epoch=redis.call('HGET',meta,'epoch'); e.redisTimeMilliseconds=now; encoded=cjson.encode(e)
          if redis.call('LLEN',events)>=maxCount or bytes+#encoded>maxBytes then return false end
          if #encoded>81920 or bytes+#encoded>maxBytes then return false end
          redis.call('HSET',meta,'seq',seq); redis.call('RPUSH',events,encoded); redis.call('HINCRBY',meta,'bytes',#encoded); redis.call('PUBLISH',meta..':wake','1'); return seq
        end
        local now=clock(); local maxPrune=tonumber(ARGV[5]); local lease=tonumber(ARGV[7]); local ttl=tonumber(ARGV[6])
        local candidate=cjson.decode(ARGV[1]); local epoch=redis.call('HGET',meta,'epoch')
        if not epoch then
          if redis.call('EXISTS',KEYS[2])==1 or redis.call('EXISTS',KEYS[3])==1 or redis.call('EXISTS',KEYS[4])==1 or redis.call('EXISTS',KEYS[5])==1 then redis.call('DEL',unpack(KEYS,1,5)); return {'reset'} end
          redis.call('HSET',meta,'epoch',ARGV[2],'seq',0,'bytes',0); epoch=ARGV[2]
        end
        local expired=redis.call('ZRANGEBYSCORE',expiry,'-inf',now,'LIMIT',0,maxPrune+1)
        local n=math.min(#expired,maxPrune)
        for i=1,n do local conn=expired[i]; local raw=redis.call('HGET',members,conn)
          if raw then local m=cjson.decode(raw); local p=redis.call('HGET',presence,m.clientKey); local hasPresence=p and cjson.decode(p).incarnation==m.incarnation
            if hasPresence then local e={eventId=ARGV[2]..':'..conn..':'..m.incarnation,createdUtc=m.ticket.expiresUtc,kind='departure',clientKey=m.clientKey,sender=m,method='presenceUpdated',payloadJson='{}'}
              if not append(e,now,tonumber(ARGV[8]),tonumber(ARGV[9]),tonumber(ARGV[10]),maxPrune) then touch(ttl); return {'busy'} end
            end
            redis.call('HDEL',members,conn); redis.call('ZREM',expiry,conn); redis.call('ZREM',m.subjectIndexKey,m.subjectIndexMember)
            if hasPresence then redis.call('HDEL',presence,m.clientKey) end
          else redis.call('ZREM',expiry,conn) end
        end
        if #expired>maxPrune then touch(ttl); return {'busy'} end
        local oldSubject=redis.call('ZRANGEBYSCORE',subject,'-inf',now,'LIMIT',0,maxPrune+1)
        for i=1,math.min(#oldSubject,maxPrune) do redis.call('ZREM',subject,oldSubject[i]) end
        if #oldSubject>maxPrune then touch(ttl); return {'busy'} end
        local all=redis.call('HVALS',members); local count=0
        for _,raw in ipairs(all) do local m=cjson.decode(raw); count=count+1
          if m.clientKey==candidate.clientKey then
            if m.ticket.actor.identity.subject~=candidate.ticket.actor.identity.subject then return {'denied'} end
            local p=redis.call('HGET',presence,m.clientKey); local hasPresence=p and cjson.decode(p).incarnation==m.incarnation
            if hasPresence then local e={eventId=ARGV[2]..':'..m.connectionId..':'..m.incarnation,createdUtc=candidate.ticket.expiresUtc,kind='departure',clientKey=m.clientKey,sender=m,method='presenceUpdated',payloadJson='{}'}
              if not append(e,now,tonumber(ARGV[8]),tonumber(ARGV[9]),tonumber(ARGV[10]),maxPrune) then touch(ttl); return {'busy'} end
            end
            redis.call('HDEL',members,m.connectionId); redis.call('ZREM',expiry,m.connectionId); redis.call('ZREM',m.subjectIndexKey,m.subjectIndexMember); redis.call('ZREM',subject,m.subjectIndexMember); if hasPresence then redis.call('HDEL',presence,m.clientKey) end; count=count-1
          end
        end
        if count>=tonumber(ARGV[3]) or redis.call('ZCARD',subject)>=tonumber(ARGV[4]) then touch(ttl); return {'denied'} end
        candidate.leaseExpiresUnixMilliseconds=now+lease; local memberJson=cjson.encode(candidate)
        redis.call('HSET',members,candidate.connectionId,memberJson); redis.call('ZADD',expiry,now+lease,candidate.connectionId)
        redis.call('ZADD',subject,now+lease,candidate.subjectIndexMember); redis.call('PEXPIRE',subject,lease+60000); touch(ttl)
        local ms=redis.call('HVALS',members); local ps=redis.call('HGETALL',presence); local outp={}; local outm={}
        for i=1,#ps,2 do local p=cjson.decode(ps[i+1]); p.clientKey=ps[i]; table.insert(outp,cjson.encode(p)) end
        for _,v in ipairs(ms) do table.insert(outm,v) end
        local snapshot='{"epoch":'..cjson.encode(epoch)..',"watermark":'..(redis.call('HGET',meta,'seq') or '0')..',"redisTimeMilliseconds":'..now..',"members":['..table.concat(outm,',')..'],"presence":['..table.concat(outp,',')..']}'
        return {'ok',snapshot}
        """;
    private const string SnapshotScript = """
        local meta=KEYS[1]; local members=KEYS[2]; local expiry=KEYS[3]; local presence=KEYS[4]; local events=KEYS[5]
        local t=redis.call('TIME'); local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000); local ttl=tonumber(ARGV[2]); local limit=tonumber(ARGV[1])
        local epoch=redis.call('HGET',meta,'epoch'); if not epoch then return {'missing'} end
        local dead=redis.call('ZRANGEBYSCORE',expiry,'-inf',now,'LIMIT',0,limit+1); local n=math.min(#dead,limit); local eventPruned=0
        for i=1,n do local conn=dead[i]; local raw=redis.call('HGET',members,conn); if raw then local m=cjson.decode(raw); local p=redis.call('HGET',presence,m.clientKey); local hasPresence=p and cjson.decode(p).incarnation==m.incarnation
          if hasPresence then
            local e={eventId=conn..':'..m.incarnation,createdUtc=m.ticket.expiresUtc,kind='departure',clientKey=m.clientKey,sender=m,method='presenceUpdated',payloadJson='{}'}; e.sequence=tonumber(redis.call('HGET',meta,'seq') or '0')+1; e.epoch=epoch; e.redisTimeMilliseconds=now; local encoded=cjson.encode(e); local bytes=tonumber(redis.call('HGET',meta,'bytes') or '0')
            while eventPruned<limit do local old=redis.call('LINDEX',events,0); if not old then break end; local prior=cjson.decode(old); local mustTrim=now-tonumber(prior.redisTimeMilliseconds or 0)>tonumber(ARGV[5]) or redis.call('LLEN',events)>=tonumber(ARGV[3]) or bytes+#encoded>tonumber(ARGV[4]); if not mustTrim then break end; redis.call('LPOP',events); bytes=bytes-#old; eventPruned=eventPruned+1 end
            redis.call('HSET',meta,'bytes',bytes); local head=redis.call('LINDEX',events,0); if head and now-tonumber(cjson.decode(head).redisTimeMilliseconds or 0)>tonumber(ARGV[5]) then for k=1,5 do redis.call('PEXPIRE',KEYS[k],ttl) end; return {'busy'} end
            if redis.call('LLEN',events)>=tonumber(ARGV[3]) or bytes+#encoded>tonumber(ARGV[4]) or #encoded>81920 then for k=1,5 do redis.call('PEXPIRE',KEYS[k],ttl) end; return {'busy'} end
            redis.call('HSET',meta,'seq',e.sequence); redis.call('RPUSH',events,encoded); redis.call('HINCRBY',meta,'bytes',#encoded); redis.call('PUBLISH',meta..':wake','1')
          end
          redis.call('HDEL',members,conn); redis.call('ZREM',m.subjectIndexKey,m.subjectIndexMember); if hasPresence then redis.call('HDEL',presence,m.clientKey) end
        end; redis.call('ZREM',expiry,conn) end
        for i=1,5 do redis.call('PEXPIRE',KEYS[i],ttl) end
        if #dead>limit then return {'busy'} end
        local ms=redis.call('HVALS',members); local ps=redis.call('HGETALL',presence); local outm={}; local outp={}
        for _,v in ipairs(ms) do table.insert(outm,cjson.decode(v)) end
        for i=1,#ps,2 do local p=cjson.decode(ps[i+1]); p.clientKey=ps[i]; table.insert(outp,p) end
        local memberJson={}; local presenceJson={}
        for _,m in ipairs(outm) do table.insert(memberJson,cjson.encode(m)) end
        for _,p in ipairs(outp) do table.insert(presenceJson,cjson.encode(p)) end
        local snapshot='{"epoch":'..cjson.encode(epoch)..',"watermark":'..(redis.call('HGET',meta,'seq') or '0')..',"redisTimeMilliseconds":'..now..',"members":['..table.concat(memberJson,',')..'],"presence":['..table.concat(presenceJson,',')..']}'
        return {'ok',snapshot}
        """;
    private const string ReadEventsScript = """
        local meta=KEYS[1]; local events=KEYS[5]; local t=redis.call('TIME'); local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
        local epoch=redis.call('HGET',meta,'epoch'); if not epoch then return {'missing','','0',tostring(now),'[]','0','0'} end
        if epoch~=ARGV[1] then return {'reset',epoch,redis.call('HGET',meta,'seq') or '0',tostring(now),'[]','1','0'} end
        local limit=tonumber(ARGV[5]); local age=tonumber(ARGV[8]); local pruned=0; local bytes=tonumber(redis.call('HGET',meta,'bytes') or '0')
        while pruned<limit do local raw=redis.call('LINDEX',events,0); if not raw then break end; local e=cjson.decode(raw); if now-tonumber(e.redisTimeMilliseconds or 0)<=age then break end; redis.call('LPOP',events); bytes=bytes-#raw; pruned=pruned+1 end
        redis.call('HSET',meta,'bytes',bytes); for i=1,5 do redis.call('PEXPIRE',KEYS[i],tonumber(ARGV[9])) end
        local head=redis.call('LINDEX',events,0); if head and now-tonumber(cjson.decode(head).redisTimeMilliseconds or 0)>age then return {'busy',epoch,redis.call('HGET',meta,'seq') or '0',tostring(now),'[]','0','1'} end
        local watermark=tonumber(redis.call('HGET',meta,'seq') or '0'); local first=head and tonumber(cjson.decode(head).sequence) or 0
        if not head and tonumber(ARGV[2])<watermark then return {'ok',epoch,tostring(watermark),tostring(now),'[]','1','0'} end
        if tonumber(ARGV[2])>watermark then return {'ok',epoch,tostring(watermark),tostring(now),'[]','1','0'} end
        if first>tonumber(ARGV[2])+1 then return {'ok',epoch,tostring(watermark),tostring(now),'[]','1','0'} end
        local out={}; local total=0
        if tonumber(ARGV[2])<watermark then
          local offset=math.max(0,tonumber(ARGV[2])+1-first)
          local batch=redis.call('LRANGE',events,offset,offset+tonumber(ARGV[3])-1)
          for _,raw in ipairs(batch) do if total+#raw>tonumber(ARGV[4]) then break end; table.insert(out,raw); total=total+#raw end
        end
        return {'ok',epoch,tostring(watermark),tostring(now),'['..table.concat(out,',')..']','0','0'}
        """;
    private const string ObserveMembersScript = """
        local meta=KEYS[1]; local members=KEYS[2]; local expiry=KEYS[3]; local presence=KEYS[4]; local events=KEYS[5]
        local t=redis.call('TIME'); local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000); local epoch=redis.call('HGET',meta,'epoch'); if not epoch then return {'missing','','[]',tostring(now)} end
        local dead=redis.call('ZRANGEBYSCORE',expiry,'-inf',now,'LIMIT',0,tonumber(ARGV[1])+1); local n=math.min(#dead,tonumber(ARGV[1])); local eventPruned=0
        for i=1,n do local conn=dead[i]; local raw=redis.call('HGET',members,conn); if raw then local m=cjson.decode(raw); local p=redis.call('HGET',presence,m.clientKey); local hasPresence=p and cjson.decode(p).incarnation==m.incarnation
          if hasPresence then
            local e={eventId=conn..':'..m.incarnation,createdUtc=m.ticket.expiresUtc,kind='departure',clientKey=m.clientKey,sender=m,method='presenceUpdated',payloadJson='{}'}; e.sequence=tonumber(redis.call('HGET',meta,'seq') or '0')+1; e.epoch=epoch; e.redisTimeMilliseconds=now; local encoded=cjson.encode(e); local bytes=tonumber(redis.call('HGET',meta,'bytes') or '0')
            while eventPruned<tonumber(ARGV[1]) do local old=redis.call('LINDEX',events,0); if not old then break end; local prior=cjson.decode(old); local mustTrim=now-tonumber(prior.redisTimeMilliseconds or 0)>30000 or redis.call('LLEN',events)>=256 or bytes+#encoded>4194304; if not mustTrim then break end; redis.call('LPOP',events); bytes=bytes-#old; eventPruned=eventPruned+1 end
            redis.call('HSET',meta,'bytes',bytes); local head=redis.call('LINDEX',events,0); if head and now-tonumber(cjson.decode(head).redisTimeMilliseconds or 0)>30000 then for k=1,5 do redis.call('PEXPIRE',KEYS[k],tonumber(ARGV[2])) end; return {'busy',epoch,'[]',tostring(now)} end
            if redis.call('LLEN',events)>=256 or bytes+#encoded>4194304 or #encoded>81920 then for k=1,5 do redis.call('PEXPIRE',KEYS[k],tonumber(ARGV[2])) end; return {'busy',epoch,'[]',tostring(now)} end
            redis.call('HSET',meta,'seq',e.sequence); redis.call('RPUSH',events,encoded); redis.call('HINCRBY',meta,'bytes',#encoded); redis.call('PUBLISH',meta..':wake','1')
          end
          redis.call('HDEL',members,conn); redis.call('ZREM',m.subjectIndexKey,m.subjectIndexMember); if hasPresence then redis.call('HDEL',presence,m.clientKey) end
        end; redis.call('ZREM',expiry,conn) end
        if #dead>tonumber(ARGV[1]) then return {'busy',epoch,'[]',tostring(now)} end
        for i=1,5 do redis.call('PEXPIRE',KEYS[i],tonumber(ARGV[2])) end
        local out={}; for _,raw in ipairs(redis.call('HVALS',members)) do table.insert(out,cjson.decode(raw)) end
        local encoded={}; for _,m in ipairs(out) do table.insert(encoded,cjson.encode(m)) end
        return {'ok',epoch,'['..table.concat(encoded,',')..']',tostring(now)}
        """;
    private const string MutateScript = """
        local meta=KEYS[1]; local members=KEYS[2]; local expiry=KEYS[3]; local presence=KEYS[4]; local events=KEYS[5]
        local t=redis.call('TIME'); local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000); local epoch=redis.call('HGET',meta,'epoch'); if not epoch then return {'reset','','0',tostring(now)} end
        if epoch~=ARGV[1] then return {'reset',epoch,'0',tostring(now)} end
        local raw=redis.call('HGET',members,ARGV[3]); if not raw then return {'missing',epoch,'0',tostring(now)} end; local sender=cjson.decode(raw)
        if sender.incarnation~=ARGV[2] or tonumber(sender.leaseExpiresUnixMilliseconds or 0)<=now then return {'missing',epoch,'0',tostring(now)} end
        local limit=tonumber(ARGV[8]); local pruned=0; local bytes=tonumber(redis.call('HGET',meta,'bytes') or '0')
        local e=cjson.decode(ARGV[7]); local seq=tonumber(redis.call('HGET',meta,'seq') or '0')+1; e.sequence=seq; e.epoch=epoch; e.redisTimeMilliseconds=now; e.sender=sender; local encoded=cjson.encode(e)
        if #encoded>81920 then return {'denied',epoch,'0',tostring(now)} end
        while pruned<limit do local old=redis.call('LINDEX',events,0); if not old then break end; local prior=cjson.decode(old); local mustTrim=now-tonumber(prior.redisTimeMilliseconds or 0)>tonumber(ARGV[11]) or redis.call('LLEN',events)>=tonumber(ARGV[9]) or bytes+#encoded>tonumber(ARGV[10]); if not mustTrim then break end; redis.call('LPOP',events); bytes=bytes-#old; pruned=pruned+1 end
        redis.call('HSET',meta,'bytes',bytes); local first=redis.call('LINDEX',events,0); if first and now-tonumber(cjson.decode(first).redisTimeMilliseconds or 0)>tonumber(ARGV[11]) then return {'busy',epoch,'0',tostring(now)} end
        if redis.call('LLEN',events)>=tonumber(ARGV[9]) or bytes+#encoded>tonumber(ARGV[10]) then return {'busy',epoch,'0',tostring(now)} end
        if ARGV[5]=='presence' then redis.call('HSET',presence,ARGV[6],cjson.encode({clientKey=ARGV[6],incarnation=sender.incarnation,payload=e.payloadJson})) end
        redis.call('HSET',meta,'seq',seq); redis.call('RPUSH',events,encoded); redis.call('HINCRBY',meta,'bytes',#encoded); redis.call('PUBLISH',meta..':wake','1'); for i=1,5 do redis.call('PEXPIRE',KEYS[i],tonumber(ARGV[12])) end
        return {'ok',epoch,tostring(seq),tostring(now)}
        """;
    private const string HeartbeatScript = """
        local meta=KEYS[1]; local members=KEYS[2]; local expiry=KEYS[3]; local subjectJson=ARGV[1]; local wanted=cjson.decode(subjectJson)
        local t=redis.call('TIME'); local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000); local epoch=redis.call('HGET',meta,'epoch'); if not epoch then return {'missing','[]',tostring(now)} end
        local all=redis.call('HVALS',members); local current={}; local requested={}; for _,inc in ipairs(wanted) do requested[inc]=true end; local lease=tonumber(ARGV[2])
        local function stringArray(values) local encoded={}; for _,value in ipairs(values) do table.insert(encoded,cjson.encode(value)) end; return '['..table.concat(encoded,',')..']' end
        for _,raw in ipairs(all) do local m=cjson.decode(raw); local inc=m.incarnation; if requested[inc] and tonumber(m.leaseExpiresUnixMilliseconds or 0)>now then m.leaseExpiresUnixMilliseconds=now+lease; redis.call('HSET',members,m.connectionId,cjson.encode(m)); redis.call('ZADD',expiry,now+lease,m.connectionId); redis.call('ZADD',m.subjectIndexKey,now+lease,m.subjectIndexMember); redis.call('PEXPIRE',m.subjectIndexKey,lease+60000); table.insert(current,inc) end end
        local encoded={}; for _,inc in ipairs(current) do table.insert(encoded,cjson.encode(inc)) end; local currentJson='['..table.concat(encoded,',')..']'
        if #current~=#wanted then return {'ok',currentJson,tostring(now)} end
        for i=1,5 do redis.call('PEXPIRE',KEYS[i],tonumber(ARGV[3])) end
        return {'ok',currentJson,tostring(now)}
        """;
    private const string RemoveScript = """
        local meta=KEYS[1]; local members=KEYS[2]; local expiry=KEYS[3]; local presence=KEYS[4]; local events=KEYS[5]
        local t=redis.call('TIME'); local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000); local epoch=redis.call('HGET',meta,'epoch'); if not epoch then return {'missing','','0',tostring(now)} end
        local raw=redis.call('HGET',members,ARGV[2]); if not raw then return {'missing',epoch,'0',tostring(now)} end; local m=cjson.decode(raw); if m.incarnation~=ARGV[1] then return {'missing',epoch,'0',tostring(now)} end
        local p=ARGV[3]~='' and redis.call('HGET',presence,ARGV[3]) or false; local announce=p and cjson.decode(p).incarnation==m.incarnation
        local seq=0
        if announce then
          local e=cjson.decode(ARGV[5]); seq=tonumber(redis.call('HGET',meta,'seq') or '0')+1; e.sequence=seq; e.epoch=epoch; e.redisTimeMilliseconds=now; e.sender=m; local encoded=cjson.encode(e)
          local limit=tonumber(ARGV[6]); local pruned=0; local bytes=tonumber(redis.call('HGET',meta,'bytes') or '0')
          while pruned<limit do local old=redis.call('LINDEX',events,0); if not old then break end; local prior=cjson.decode(old); local mustTrim=now-tonumber(prior.redisTimeMilliseconds or 0)>tonumber(ARGV[9]) or redis.call('LLEN',events)>=tonumber(ARGV[7]) or bytes+#encoded>tonumber(ARGV[8]); if not mustTrim then break end; redis.call('LPOP',events); bytes=bytes-#old; pruned=pruned+1 end
          redis.call('HSET',meta,'bytes',bytes); local first=redis.call('LINDEX',events,0); if first and now-tonumber(cjson.decode(first).redisTimeMilliseconds or 0)>tonumber(ARGV[9]) then return {'busy',epoch,'0',tostring(now)} end
          if redis.call('LLEN',events)>=tonumber(ARGV[7]) or bytes+#encoded>tonumber(ARGV[8]) or #encoded>81920 then return {'busy',epoch,'0',tostring(now)} end
          redis.call('HDEL',members,ARGV[2]); redis.call('ZREM',expiry,ARGV[2]); redis.call('ZREM',m.subjectIndexKey,m.subjectIndexMember)
          redis.call('HDEL',presence,ARGV[3]); redis.call('HSET',meta,'seq',seq); redis.call('RPUSH',events,encoded); redis.call('HINCRBY',meta,'bytes',#encoded); redis.call('PUBLISH',meta..':wake','1')
        end
        if not announce then redis.call('HDEL',members,ARGV[2]); redis.call('ZREM',expiry,ARGV[2]); redis.call('ZREM',m.subjectIndexKey,m.subjectIndexMember) end
        for i=1,5 do redis.call('PEXPIRE',KEYS[i],tonumber(ARGV[10])) end
        return {'ok',epoch,tostring(seq),tostring(now)}
        """;
}
