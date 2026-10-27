using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.FssHttp.Tests;

public sealed class FssHttpLockCoordinatorTests
{
    [Fact]
    public void LockExecutionAcceptsXmlSchemaIntegerWhitespaceAndLeadingPlus()
    {
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        var response = new FssHttpSubResponse();

        var result = coordinator.ApplyExclusiveLock(Request(
            ("ExclusiveLockID", Guid.NewGuid().ToString("D")),
            ("ExclusiveLockRequestType", "GetLock"),
            ("Timeout", " +60 ")), response);

        Assert.Equal(LockOperationResult.Granted, result);
        Assert.Null(response.ErrorCode);
    }

    [Fact]
    public void CoauthorLifecycleSharesLockAndReleasesItAfterLastExit()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var schema = Guid.NewGuid().ToString();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        FssHttpSubRequest Session(Guid client, string operation) => Request(
            ("ClientID", client.ToString()), ("SchemaLockID", schema), ("CoauthRequestType", operation));
        coordinator.ApplyCoauthSession(document, Session(first, "JoinCoauthoring"), new());
        var joined = new FssHttpSubResponse();
        coordinator.ApplyCoauthSession(document, Session(second, "JoinCoauthoring"), joined);
        Assert.Equal("Coauthoring", joined.SubResponseDataAttributes["CoauthStatus"]);
        Assert.Equal(2, document.Sessions.Count);
        var lockRequest = Request(("ExclusiveLockID", Guid.NewGuid().ToString()), ("ExclusiveLockRequestType", "GetLock"));
        Assert.Equal(LockOperationResult.Conflict, coordinator.ApplyExclusiveLock(lockRequest, new()));
        coordinator.ApplyCoauthSession(document, Session(first, "ExitCoauthoring"), new());
        coordinator.ApplyCoauthSession(document, Session(second, "ExitCoauthoring"), new());
        Assert.Empty(document.Sessions);
        Assert.Equal(LockOperationResult.Granted, coordinator.ApplyExclusiveLock(lockRequest, new()));
    }

    [Fact]
    public void SchemaLock_AllowsAnotherClientWithTheSameSchemaId()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var lockId = Guid.NewGuid();

        var granted = coordinator.ApplySchemaLock(
            Request(("ClientID", first.ToString("D")), ("SchemaLockID", lockId.ToString("D")), ("SchemaLockRequestType", "GetLock")),
            new FssHttpSubResponse());
        var conflictResponse = new FssHttpSubResponse();
        var conflict = coordinator.ApplySchemaLock(
            Request(("ClientID", second.ToString("D")), ("SchemaLockID", lockId.ToString("D")), ("SchemaLockRequestType", "GetLock")), conflictResponse);

        Assert.Equal(LockOperationResult.Granted, granted);
        Assert.Equal(LockOperationResult.Granted, conflict);
        Assert.Null(conflictResponse.ErrorCode);
    }

    [Fact]
    public void SchemaRefreshReacquiresExpiredLockAfterCoordinatorRecreation()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var client = Guid.NewGuid().ToString();
        var schema = Guid.NewGuid().ToString();
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        Assert.Equal(LockOperationResult.Granted, coordinator.ApplySchemaLock(Request(
            ("ClientID", client), ("SchemaLockID", schema), ("SchemaLockRequestType", "GetLock")), new(), now));
        var expiredAt = now.AddSeconds(3600);
        var restored = FssHttpLockCoordinator.Restore(NewDocument(), coordinator.Capture(), expiredAt, TestActor.Value.Identity);
        var response = new FssHttpSubResponse();

        Assert.Equal(LockOperationResult.Granted, restored.ApplySchemaLock(Request(
            ("ClientID", client), ("SchemaLockID", schema), ("SchemaLockRequestType", "RefreshLock")), response));

        Assert.Null(response.ErrorCode);
        Assert.Equal("SchemaLock", response.SubResponseDataAttributes["LockType"]);
        var lease = Assert.Single(restored.Capture().SchemaOwners);
        Assert.Equal(client, lease.Client);
        Assert.Equal(schema, lease.Id);
        Assert.Equal(TestActor.Value.Identity.Subject, lease.OwnerSubject);
        Assert.Equal(expiredAt.AddSeconds(3600), lease.ExpiresUtc);
        Assert.Equal(3600, lease.TimeoutSeconds);
        Assert.Empty(restored.Capture().CoauthorClients);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(4000)]
    public void SchemaRefreshWithLowerTimeoutIsNoOpAfterPersistence(int elapsedSeconds)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var client = Guid.NewGuid().ToString();
        var schema = Guid.NewGuid().ToString();
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        Assert.Equal(LockOperationResult.Granted, coordinator.ApplySchemaLock(Request(
            ("ClientID", client), ("SchemaLockID", schema), ("SchemaLockRequestType", "GetLock"),
            ("Timeout", "7200")), new(), now));
        var state = JsonSerializer.Deserialize<CoordinationState>(
            JsonSerializer.Serialize(coordinator.Capture()))!;
        coordinator = FssHttpLockCoordinator.Restore(NewDocument(), state, actor: TestActor.Value.Identity);

        Assert.Equal(LockOperationResult.Refreshed, coordinator.ApplySchemaLock(Request(
            ("ClientID", client), ("SchemaLockID", schema), ("SchemaLockRequestType", "RefreshLock")), new(), now.AddSeconds(elapsedSeconds)));
        Assert.Equal(now.AddSeconds(7200), Assert.Single(coordinator.Capture().SchemaOwners).ExpiresUtc);
        Assert.Equal(7200, Assert.Single(coordinator.Capture().SchemaOwners).TimeoutSeconds);

        Assert.Equal(LockOperationResult.Refreshed, coordinator.ApplySchemaLock(Request(
            ("ClientID", client), ("SchemaLockID", schema), ("SchemaLockRequestType", "RefreshLock"),
            ("Timeout", "7200")), new(), now.AddSeconds(elapsedSeconds + 60)));
        Assert.Equal(now.AddSeconds(elapsedSeconds + 7260), Assert.Single(coordinator.Capture().SchemaOwners).ExpiresUtc);
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(4000, true)]
    public void LegacySchemaLeaseRefreshPreservesExpiryAndLearnsDurationOnExtension(int elapsedSeconds, bool extends)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var client = Guid.NewGuid().ToString();
        var schema = Guid.NewGuid().ToString();
        var legacy = new LeaseState(schema, client, now.AddSeconds(7200), 0, null,
            TestActor.Value.Identity.Subject);
        var json = JsonNode.Parse(JsonSerializer.Serialize(legacy))!;
        json.AsObject().Remove("TimeoutSeconds");
        legacy = json.Deserialize<LeaseState>()!;
        Assert.Null(legacy.TimeoutSeconds);
        var state = new CoordinationState(schema, [legacy], null, 0);
        var coordinator = FssHttpLockCoordinator.Restore(NewDocument(), state, actor: TestActor.Value.Identity);

        Assert.Equal(LockOperationResult.Refreshed, coordinator.ApplySchemaLock(Request(
            ("ClientID", client), ("SchemaLockID", schema), ("SchemaLockRequestType", "RefreshLock")), new(), now.AddSeconds(elapsedSeconds)));

        var lease = Assert.Single(coordinator.Capture().SchemaOwners);
        Assert.Equal(now.AddSeconds(extends ? elapsedSeconds + 3600 : 7200), lease.ExpiresUtc);
        Assert.Equal(extends ? 3600 : (int?)null, lease.TimeoutSeconds);
    }

    [Fact]
    public void SchemaRefreshReacquiresExpiredClientWhilePeerStillHoldsLock()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var schema = Guid.NewGuid().ToString();
        var client = Guid.NewGuid().ToString();
        var peer = Guid.NewGuid().ToString();
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        foreach (var (id, timeout) in new[] { (client, "3600"), (peer, "7200") })
            Assert.Equal(LockOperationResult.Granted, coordinator.ApplySchemaLock(Request(
                ("ClientID", id), ("SchemaLockID", schema), ("SchemaLockRequestType", "GetLock"), ("Timeout", timeout)), new(), now));
        var response = new FssHttpSubResponse();

        Assert.Equal(LockOperationResult.Granted, coordinator.ApplySchemaLock(Request(
            ("ClientID", client), ("SchemaLockID", schema), ("SchemaLockRequestType", "RefreshLock")), response, now.AddSeconds(3600)));

        Assert.Null(response.ErrorCode);
        Assert.Equal("SchemaLock", response.SubResponseDataAttributes["LockType"]);
        var leases = coordinator.Capture().SchemaOwners;
        Assert.Equal(2, leases.Length);
        Assert.Equal(now.AddSeconds(7200), Assert.Single(leases, lease => lease.Client == peer).ExpiresUtc);
        var renewed = Assert.Single(leases, lease => lease.Client == client);
        Assert.Equal(now.AddSeconds(7200), renewed.ExpiresUtc);
        Assert.Equal(3600, renewed.TimeoutSeconds);
        Assert.Equal(TestActor.Value.Identity.Subject, renewed.OwnerSubject);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SchemaRefreshRejectsCompetingLockWithoutChangingAuthority(bool exclusive)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        var schema = Guid.NewGuid().ToString();
        var client = Guid.NewGuid().ToString();
        var granted = exclusive
            ? coordinator.ApplyExclusiveLock(Request(("ExclusiveLockID", Guid.NewGuid().ToString()),
                ("ExclusiveLockRequestType", "GetLock")), new(), now)
            : coordinator.ApplySchemaLock(Request(("ClientID", Guid.NewGuid().ToString()),
                ("SchemaLockID", Guid.NewGuid().ToString()), ("SchemaLockRequestType", "GetLock")), new(), now);
        Assert.Equal(LockOperationResult.Granted, granted);
        var before = JsonSerializer.Serialize(coordinator.Capture());
        var response = new FssHttpSubResponse();

        Assert.Equal(LockOperationResult.Conflict, coordinator.ApplySchemaLock(Request(
            ("ClientID", client), ("SchemaLockID", schema), ("SchemaLockRequestType", "RefreshLock")), response, now));

        Assert.Equal("FileAlreadyLockedOnServer", response.ErrorCode);
        Assert.Equal(before, JsonSerializer.Serialize(coordinator.Capture()));
    }

    [Fact]
    public void ExclusiveLock_RequiresTheSchemaOwnerAndCanBeReleasedByToken()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var schemaId = Guid.NewGuid();

        Assert.Equal(LockOperationResult.Granted, coordinator.ApplySchemaLock(
            Request(("ClientID", owner.ToString()), ("SchemaLockID", schemaId.ToString()), ("SchemaLockRequestType", "GetLock")),
            new FssHttpSubResponse()));

        var denied = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Conflict, coordinator.ApplyExclusiveLock(
            Request(("ClientID", other.ToString()), ("ExclusiveLockID", Guid.NewGuid().ToString()), ("ExclusiveLockRequestType", "GetLock")), denied));

        var exclusive = new FssHttpSubResponse();
        var exclusiveId = Guid.NewGuid();
        var convert = Request(("ClientID", owner.ToString()), ("SchemaLockID", schemaId.ToString()),
            ("ExclusiveLockID", exclusiveId.ToString()), ("SchemaLockRequestType", "ConvertToExclusive"));
        Assert.Equal(LockOperationResult.Completed, coordinator.ApplySchemaLock(convert, exclusive));

        var released = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Released, coordinator.ApplyExclusiveLock(
            Request(("ExclusiveLockID", exclusiveId.ToString()), ("ExclusiveLockRequestType", "ReleaseLock")), released));
        Assert.Null(released.ErrorCode);
    }

    [Fact]
    public void ExclusiveLockTokenAcceptsEquivalentGuidRepresentationsAcrossRequests()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var lockId = Guid.NewGuid();
        var canonical = lockId.ToString("D").ToUpperInvariant();

        Assert.Equal(LockOperationResult.Granted, coordinator.ApplyExclusiveLock(
            Request(("ExclusiveLockID", lockId.ToString("B")),
                ("ExclusiveLockRequestType", "GetLock")),
            new FssHttpSubResponse()));

        var refreshed = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Refreshed, coordinator.ApplyExclusiveLock(
            Request(("ExclusiveLockID", canonical),
                ("ExclusiveLockRequestType", "RefreshLock")),
            refreshed));
        Assert.Null(refreshed.ErrorCode);

        var released = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Released, coordinator.ApplyExclusiveLock(
            Request(("ExclusiveLockID", lockId.ToString("N")),
                ("ExclusiveLockRequestType", "ReleaseLock")),
            released));
        Assert.Null(released.ErrorCode);
    }

    [Fact]
    public void SchemaLockAndCoauthorExitAcceptEquivalentGuidRepresentations()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var schemaId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        Assert.Equal(LockOperationResult.Granted, coordinator.ApplyCoauthSession(
            document,
            Request(("ClientID", clientId.ToString("B")),
                ("SchemaLockID", schemaId.ToString("D").ToUpperInvariant()),
                ("CoauthRequestType", "JoinCoauthoring")),
            new FssHttpSubResponse()));

        var exited = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Released, coordinator.ApplyCoauthSession(
            document,
            Request(("ClientID", clientId.ToString("N")),
                ("SchemaLockID", schemaId.ToString("N")),
                ("CoauthRequestType", "ExitCoauthoring")),
            exited));
        Assert.Null(exited.ErrorCode);
        Assert.Empty(document.Sessions);
    }

    [Fact]
    public void LockStatus_ReportsExpiryAsUnlocked()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var response = new FssHttpSubResponse();
        var at = DateTime.UtcNow;
        var owner = Guid.NewGuid();

        Assert.Equal(LockOperationResult.Granted, coordinator.ApplySchemaLock(
            Request(("ClientID", owner.ToString()), ("SchemaLockID", Guid.NewGuid().ToString()), ("SchemaLockRequestType", "GetLock"), ("Timeout", "3600")),
            new FssHttpSubResponse(), at));
        coordinator.ApplyLockStatus(Request(), response, at.AddSeconds(3601));

        Assert.Equal("0", response.SubResponseDataAttributes["LockType"]);
    }

    [Fact]
    public void AmIAloneUsesTheFileTransitionIdentityAndCoauthorMembership()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        coordinator.ApplyCoauthSession(document, Request(("ClientID", owner.ToString()),
            ("SchemaLockID", Guid.NewGuid().ToString()), ("CoauthRequestType", "JoinCoauthoring")), new());

        var alone = new FssHttpSubResponse();
        coordinator.ApplyAmIAlone(document, Request(("TransitionID", document.TransitionId.ToString())), alone);
        Assert.Equal("True", alone.SubResponseDataAttributes["AmIAlone"]);

        var unknown = new FssHttpSubResponse();
        coordinator.ApplyAmIAlone(document, Request(("TransitionID", Guid.NewGuid().ToString())), unknown);
        Assert.Equal("InvalidArgument", unknown.ErrorCode);
    }

    [Fact]
    public void SchemaOwnerWithoutCoauthorMembershipCannotUseCoauthConversion()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var schemaId = Guid.NewGuid().ToString();
        var exclusiveId = Guid.NewGuid().ToString();
        var request = Request(("ClientID", owner.ToString()), ("SchemaLockID", schemaId), ("ExclusiveLockID", exclusiveId), ("SchemaLockRequestType", "GetLock"));
        var response = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Granted, coordinator.ApplySchemaLock(request, response));
        var rejected = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Conflict, coordinator.ApplyCoauthTransition(
            Request(("ClientID", owner.ToString()), ("SchemaLockID", schemaId),
                ("ExclusiveLockID", exclusiveId), ("CoauthRequestType", "ConvertToExclusive")), rejected));
        Assert.Equal("InvalidCoauthSession", rejected.ErrorCode);
    }

    [Fact]
    public void CellWriteRunsWithoutAnActiveLock()
    {
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);

        var allowed = coordinator.ExecuteCellWrite(
            new Dictionary<string, string>(),
            () => "written",
            out string? value,
            out string? errorCode);

        Assert.True(allowed);
        Assert.Equal("written", value);
        Assert.Null(errorCode);
        Assert.Null(coordinator.CellWriteDeadline);
    }

    [Fact]
    public void CellWriteReportsTheMatchingExclusiveLeaseDeadline()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        var lockId = Guid.NewGuid();
        Assert.Equal(LockOperationResult.Granted, coordinator.ApplyExclusiveLock(Request(
            ("ExclusiveLockID", lockId.ToString("D")), ("ExclusiveLockRequestType", "GetLock"),
            ("Timeout", "60")), new(), now));

        Assert.True(coordinator.ExecuteCellWrite(
            new Dictionary<string, string> { ["ExclusiveLockID"] = lockId.ToString("N") },
            () => coordinator.CellWriteDeadline, out DateTime? deadline, out string? errorCode, now));

        Assert.Null(errorCode);
        Assert.Equal(now.AddSeconds(60), deadline);
        Assert.Equal(deadline, coordinator.CellWriteDeadline);
    }

    [Theory]
    [InlineData(true, 120, 60, 120)]
    [InlineData(true, 60, 120, 60)]
    [InlineData(false, 120, 60, 120)]
    public void CellWriteUsesTheMatchingSchemaOwnerLease(bool includeClient, int ownerSeconds, int peerSeconds, int expectedSeconds)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var schema = Guid.NewGuid().ToString("D");
        var owner = Guid.NewGuid().ToString("D");
        var peer = Guid.NewGuid().ToString("D");
        var foreign = Guid.NewGuid().ToString("D");
        var state = new CoordinationState(schema,
        [
            new(schema, owner, now.AddSeconds(ownerSeconds), 0, schema, TestActor.Value.Identity.Subject),
            new(schema, peer, now.AddSeconds(peerSeconds), 0, schema, TestActor.Value.Identity.Subject),
            new(schema, foreign, now.AddSeconds(180), 0, schema, "other-subject"),
        ], null, 0);
        var coordinator = FssHttpLockCoordinator.Restore(NewDocument(), state, now, TestActor.Value.Identity);
        var attributes = new Dictionary<string, string> { ["SchemaLockID"] = schema };
        if (includeClient) attributes["ClientID"] = owner;

        Assert.True(coordinator.ExecuteCellWrite(attributes, () => coordinator.CellWriteDeadline,
            out DateTime? deadline, out string? errorCode, now));

        Assert.Null(errorCode);
        Assert.Equal(now.AddSeconds(expectedSeconds), deadline);
        Assert.Equal(deadline, coordinator.CellWriteDeadline);
    }

    [Fact]
    public void CellWriteRequiresTheActiveSchemaLockAndOptionalClientOwner()
    {
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        var owner = Guid.NewGuid();
        var schema = Guid.NewGuid();
        var schemaAttributes = new Dictionary<string, string>
        {
            ["ClientID"] = owner.ToString("D"),
            ["SchemaLockID"] = schema.ToString("D"),
            ["SchemaLockRequestType"] = "GetLock",
        };
        Assert.Equal(LockOperationResult.Granted,
            coordinator.ApplySchemaLock(Request(schemaAttributes), new FssHttpSubResponse()));

        var invoked = false;
        var denied = coordinator.ExecuteCellWrite(
            new Dictionary<string, string> { ["SchemaLockID"] = Guid.NewGuid().ToString("N") },
            () => invoked = true,
            out bool? _,
            out string? deniedCode);
        Assert.False(denied);
        Assert.False(invoked);
        Assert.Equal("FileAlreadyLockedOnServer", deniedCode);

        var allowed = coordinator.ExecuteCellWrite(
            new Dictionary<string, string>
            {
                ["BypassLockID"] = schema.ToString("N"),
                ["ClientID"] = owner.ToString("B"),
            },
            () => invoked = true,
            out bool? _,
            out string? allowedCode);
        Assert.True(allowed);
        Assert.True(invoked);
        Assert.Null(allowedCode);

        var conflicting = coordinator.ExecuteCellWrite(
            new Dictionary<string, string>
            {
                ["SchemaLockID"] = schema.ToString("D"),
                ["BypassLockID"] = Guid.NewGuid().ToString("D"),
            },
            () => true,
            out bool? _,
            out string? conflictingCode);
        Assert.False(conflicting);
        Assert.Equal("FileAlreadyLockedOnServer", conflictingCode);
    }

    [Fact]
    public void CellWriteRequiresTheActiveExclusiveLockAndExpiresIt()
    {
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        var lockId = Guid.NewGuid();
        var grantedAt = DateTime.UtcNow;
        Assert.Equal(LockOperationResult.Granted, coordinator.ApplyExclusiveLock(
            Request(("ExclusiveLockID", lockId.ToString("D")),
                ("ExclusiveLockRequestType", "GetLock"), ("Timeout", "60")),
            new FssHttpSubResponse(), grantedAt));

        var denied = coordinator.ExecuteCellWrite(
            new Dictionary<string, string>
            {
                ["ExclusiveLockID"] = Guid.NewGuid().ToString("D"),
            },
            () => 1,
            out int? _,
            out string? deniedCode,
            grantedAt.AddMilliseconds(500));
        Assert.False(denied);
        Assert.Equal("FileAlreadyLockedOnServer", deniedCode);

        var afterExpiry = coordinator.ExecuteCellWrite(
            new Dictionary<string, string>(),
            () => 2,
            out int? value,
            out string? allowedCode,
            grantedAt.AddSeconds(61));
        Assert.True(afterExpiry);
        Assert.Equal(2, value);
        Assert.Null(allowedCode);
        Assert.Null(coordinator.CellWriteDeadline);
    }

    [Fact]
    public void CellWritesAreSerializedPerDocument()
    {
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        var active = 0;
        var maximum = 0;
        using var firstEntered = new ManualResetEventSlim();
        using var secondAttempting = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();

        object Write(ManualResetEventSlim? entered = null)
        {
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maximum);
                if (observed >= current || Interlocked.CompareExchange(ref maximum, current, observed) == observed)
                    break;
            }
            entered?.Set();
            if (entered == firstEntered) releaseFirst.Wait();
            Interlocked.Decrement(ref active);
            return new object();
        }

        Exception? firstFailure = null;
        Exception? secondFailure = null;
        var firstResult = false;
        var secondResult = false;
        var secondStarted = false;
        var firstEnteredInTime = false;
        var secondAttemptedInTime = false;
        var secondBlocked = false;
        var secondEnteredBeforeRelease = false;
        var firstJoined = false;
        var secondJoined = true;
        var first = new Thread(() =>
        {
            try
            {
                firstResult = coordinator.ExecuteCellWrite(
                    new Dictionary<string, string>(), () => Write(firstEntered), out object? _, out string? _);
            }
            catch (Exception error) { firstFailure = error; }
        }) { IsBackground = true };
        var second = new Thread(() =>
        {
            try
            {
                secondAttempting.Set();
                secondResult = coordinator.ExecuteCellWrite(
                    new Dictionary<string, string>(), () => Write(secondEntered), out object? _, out string? _);
            }
            catch (Exception error) { secondFailure = error; }
        }) { IsBackground = true };
        try
        {
            first.Start();
            firstEnteredInTime = firstEntered.Wait(TimeSpan.FromSeconds(5));
            if (firstEnteredInTime)
            {
                second.Start();
                secondStarted = true;
                secondAttemptedInTime = secondAttempting.Wait(TimeSpan.FromSeconds(5));
                secondBlocked = SpinWait.SpinUntil(() =>
                    second.ThreadState.HasFlag(ThreadState.WaitSleepJoin) || secondEntered.IsSet ||
                    second.ThreadState.HasFlag(ThreadState.Stopped), TimeSpan.FromSeconds(5));
                secondEnteredBeforeRelease = secondEntered.IsSet;
            }
        }
        finally
        {
            releaseFirst.Set();
            firstJoined = first.Join(TimeSpan.FromSeconds(5));
            if (secondStarted) secondJoined = second.Join(TimeSpan.FromSeconds(5));
        }

        if (firstFailure is not null) throw new AggregateException("The first lock-test worker failed.", firstFailure);
        if (secondFailure is not null) throw new AggregateException("The second lock-test worker failed.", secondFailure);
        Assert.True(firstEnteredInTime);
        Assert.True(secondAttemptedInTime);
        Assert.True(secondBlocked);
        Assert.False(secondEnteredBeforeRelease);
        Assert.True(firstJoined);
        Assert.True(secondJoined);
        Assert.True(firstResult);
        Assert.True(secondResult);

        Assert.Equal(1, maximum);
    }

    private static StoredDocument NewDocument() =>
        new DocumentStore().GetOrAdd("/shared/test.docx");

    private static FssHttpSubRequest Request(params (string Key, string Value)[] values)
    {
        var request = new FssHttpSubRequest();
        foreach (var (key, value) in values)
            request.SubRequestDataAttributes[key] = value;
        // These lifecycle fixtures supply valid acquisition/conversion input.
        var attrs = request.SubRequestDataAttributes;
        if (attrs.Values.Any(v => v is "GetLock" or "RefreshLock" or "JoinCoauthoring" or "RefreshCoauthoring" or "ConvertToExclusive"))
            attrs.TryAdd("Timeout", "3600");
        if (attrs.Values.Contains("ConvertToExclusive"))
            attrs.TryAdd("ReleaseLockOnConversionToExclusiveFailure", "false");
        return request;
    }

    private static FssHttpSubRequest Request(IReadOnlyDictionary<string, string> values)
    {
        var request = new FssHttpSubRequest();
        foreach (var pair in values)
            request.SubRequestDataAttributes[pair.Key] = pair.Value;
        // These lifecycle fixtures supply valid acquisition/conversion input.
        var attrs = request.SubRequestDataAttributes;
        if (attrs.Values.Any(v => v is "GetLock" or "RefreshLock" or "JoinCoauthoring" or "RefreshCoauthoring" or "ConvertToExclusive"))
            attrs.TryAdd("Timeout", "3600");
        if (attrs.Values.Contains("ConvertToExclusive"))
            attrs.TryAdd("ReleaseLockOnConversionToExclusiveFailure", "false");
        return request;
    }
}
