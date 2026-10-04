using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Web;

namespace CellBridge.FssHttp.Tests;

public sealed class FssHttpLockCoordinatorTests
{
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
            Request(("ClientID", owner.ToString()), ("SchemaLockID", Guid.NewGuid().ToString()), ("SchemaLockRequestType", "GetLock"), ("Timeout", "1")),
            new FssHttpSubResponse(), at));
        coordinator.ApplyLockStatus(Request(), response, at.AddSeconds(2));

        Assert.Equal("0", response.SubResponseDataAttributes["LockType"]);
    }

    [Fact]
    public void AmIAloneRequiresTheCallerToBeAnActiveSession()
    {
        var document = NewDocument();
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        document.JoinSession(owner);

        var alone = new FssHttpSubResponse();
        coordinator.ApplyAmIAlone(document, Request(("TransitionID", document.TransitionId.ToString())), alone);
        Assert.Equal("True", alone.SubResponseDataAttributes["AmIAlone"]);

        var unknown = new FssHttpSubResponse();
        coordinator.ApplyAmIAlone(document, Request(("TransitionID", Guid.NewGuid().ToString())), unknown);
        Assert.Equal("InvalidArgument", unknown.ErrorCode);
    }

    [Fact]
    public void CoauthTransitionsAreExplicitlyUnsupportedUntilTransitionStateIsImplemented()
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
        Assert.Equal(LockOperationResult.NotSupported, coordinator.ApplyCoauthTransition(
            Request(("ClientID", owner.ToString()), ("SchemaLockID", schemaId),
                ("ExclusiveLockID", exclusiveId), ("CoauthRequestType", "ConvertToExclusive")), rejected));
        Assert.Equal("NotSupported", rejected.ErrorCode);
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
                ("ExclusiveLockRequestType", "GetLock"), ("Timeout", "1")),
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
            grantedAt.AddSeconds(2));
        Assert.True(afterExpiry);
        Assert.Equal(2, value);
        Assert.Null(allowedCode);
    }

    [Fact]
    public void CellWritesAreSerializedPerDocument()
    {
        var coordinator = FssHttpLockCoordinator.For(NewDocument(), TestActor.Value.Identity);
        var active = 0;
        var maximum = 0;

        object Write()
        {
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maximum);
                if (observed >= current || Interlocked.CompareExchange(ref maximum, current, observed) == observed)
                    break;
            }

            Thread.Sleep(25);
            Interlocked.Decrement(ref active);
            return new object();
        }

        Parallel.Invoke(
            () => Assert.True(coordinator.ExecuteCellWrite(
                new Dictionary<string, string>(), Write, out object? _, out string? _)),
            () => Assert.True(coordinator.ExecuteCellWrite(
                new Dictionary<string, string>(), Write, out object? _, out string? _)));

        Assert.Equal(1, maximum);
    }

    private static StoredDocument NewDocument() =>
        new DocumentStore().GetOrAdd("/shared/test.docx");

    private static FssHttpSubRequest Request(params (string Key, string Value)[] values)
    {
        var request = new FssHttpSubRequest();
        foreach (var (key, value) in values)
            request.SubRequestDataAttributes[key] = value;
        return request;
    }

    private static FssHttpSubRequest Request(IReadOnlyDictionary<string, string> values)
    {
        var request = new FssHttpSubRequest();
        foreach (var pair in values)
            request.SubRequestDataAttributes[pair.Key] = pair.Value;
        return request;
    }
}
