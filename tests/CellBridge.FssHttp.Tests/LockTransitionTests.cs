using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed class LockTransitionTests
{
    [Theory]
    [InlineData("ConvertToSchema", false)]
    [InlineData("ConvertToSchemaJoinCoauth", true)]
    public void ExclusiveConversionCreatesOnlyTheRequestedMembership(string operation, bool joins)
    {
        var h = new Harness();
        Assert.Equal(LockOperationResult.Granted, h.Exclusive("GetLock", new()));
        var response = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Completed, h.Exclusive(operation, response));
        var state = h.Coordinator.Capture();
        Assert.Null(state.Exclusive);
        Assert.Equal(h.Schema, state.SchemaId);
        Assert.Single(state.SchemaOwners);
        Assert.Equal(joins ? 1 : 0, state.CoauthorClients.Length);
        Assert.Equal(joins ? 1 : 0, h.Document.Sessions.Count);
        Assert.False(state.CoauthorTransitionPending);
        Assert.Equal(joins, response.SubResponseDataAttributes.ContainsKey("CoauthStatus"));
        if (joins) Assert.Equal(h.Document.TransitionId.ToString("D"), response.SubResponseDataAttributes["TransitionID"]);
    }

    [Theory]
    [InlineData("ClientID")]
    [InlineData("SchemaLockID")]
    [InlineData("Timeout")]
    public void MissingConversionInputLeavesExclusiveLeaseIntact(string missing)
    {
        var h = new Harness(); h.Exclusive("GetLock", new());
        var request = h.ExclusiveRequest("ConvertToSchema");
        request.SubRequestDataAttributes.Remove(missing);
        string before = JsonSerializer.Serialize(h.Coordinator.Capture());
        Assert.Equal(LockOperationResult.InvalidArgument, h.Coordinator.ApplyExclusiveLock(request, new()));
        Assert.Equal(before, JsonSerializer.Serialize(h.Coordinator.Capture()));
    }

    [Fact]
    public void TrackedExclusiveConversionCannotReplaceAnotherActorsReader()
    {
        var h = new Harness(); h.Exclusive("GetLock", new());
        var other = new SubjectIdentity("local:other", "other", "Other");
        h.Document.JoinEditingSession(h.Second, 3600, asEditor: false, owner: other);
        var request = h.ExclusiveRequest("ConvertToSchemaJoinCoauth");
        request.SubRequestDataAttributes["ClientID"] = h.Second.ToString();
        var before = JsonSerializer.Serialize(h.Coordinator.Capture());
        Assert.Equal(LockOperationResult.AccessDenied, h.Coordinator.ApplyExclusiveLock(request, new()));
        Assert.Equal(before, JsonSerializer.Serialize(h.Coordinator.Capture()));
        Assert.Equal(other, h.Document.GetSession(h.Second)!.Owner);
    }

    [Fact]
    public void ExclusiveConversionReportsExistingSchemaLockAsConflict()
    {
        var h = new Harness(); h.Join(h.Second);
        var response = new FssHttpSubResponse();
        h.Exclusive("ConvertToSchema", response);
        Assert.Equal("FileAlreadyLockedOnServer", response.ErrorCode);
        Assert.Single(h.Coordinator.Capture().SchemaOwners);
    }

    [Theory]
    [InlineData("ConvertToSchema", 60)]
    [InlineData("ConvertToSchemaJoinCoauth", 60)]
    public void ExclusiveRequestUsesItsOwnTimeoutPolicy(string operation, int seconds)
    {
        var h = new Harness(); var now = DateTime.UtcNow;
        h.Document.UseAuthoritativeTime(now);
        h.Coordinator.ApplyExclusiveLock(h.ExclusiveRequest("GetLock"), new(), now);
        var request = h.ExclusiveRequest(operation); request.SubRequestDataAttributes["Timeout"] = seconds.ToString();
        h.Coordinator.ApplyExclusiveLock(request, new(), now);
        Assert.Equal(now.AddSeconds(seconds), Assert.Single(h.Coordinator.Capture().SchemaOwners).ExpiresUtc);
    }

    [Fact]
    public void CoauthConversionNormalizesLowCoauthorTimeoutToDefault()
    {
        var h = new Harness(); var now = DateTime.UtcNow; h.Document.UseAuthoritativeTime(now); h.Join(h.First);
        var request = h.CoauthRequest(h.First, "ConvertToExclusive"); request.SubRequestDataAttributes["Timeout"] = "60";
        h.Coordinator.ApplyCoauthSession(h.Document, request, new(), now);
        Assert.Equal(now.AddSeconds(3600), h.Coordinator.Capture().Exclusive!.ExpiresUtc);
    }

    [Fact]
    public void PendingTransitionRemainsUntilLastMemberExpires()
    {
        var h = new Harness(); var now = DateTime.UtcNow; h.Document.UseAuthoritativeTime(now);
        var first = h.CoauthRequest(h.First, "JoinCoauthoring"); first.SubRequestDataAttributes["Timeout"] = "100";
        var second = h.CoauthRequest(h.Second, "JoinCoauthoring"); second.SubRequestDataAttributes["Timeout"] = "200";
        h.Coordinator.ApplyCoauthSession(h.Document, first, new(), now);
        h.Coordinator.ApplyCoauthSession(h.Document, second, new(), now);
        h.Coordinator.ApplyLockStatus(new(), new(), now.AddSeconds(101));
        Assert.Single(h.Coordinator.Capture().CoauthorClients);
        Assert.True(h.Coordinator.Capture().CoauthorTransitionPending);
        h.Coordinator.ApplyLockStatus(new(), new(), now.AddSeconds(201));
        Assert.Empty(h.Coordinator.Capture().CoauthorClients);
        Assert.False(h.Coordinator.Capture().CoauthorTransitionPending);
    }

    [Fact]
    public void ExclusiveConversionDistinguishesMissingAndConflictingLocks()
    {
        var h = new Harness();
        var absent = new FssHttpSubResponse();
        h.Exclusive("ConvertToSchema", absent);
        Assert.Equal("FileNotLockedOnServer", absent.ErrorCode);
        var acquired = new FssHttpSubResponse(); h.Exclusive("GetLock", acquired);
        Assert.Empty(acquired.SubResponseDataAttributes);
        var request = h.ExclusiveRequest("ConvertToSchema");
        request.SubRequestDataAttributes["ExclusiveLockID"] = Guid.NewGuid().ToString();
        var conflict = new FssHttpSubResponse(); h.Coordinator.ApplyExclusiveLock(request, conflict);
        Assert.Equal("FileAlreadyLockedOnServer", conflict.ErrorCode);
        Assert.NotNull(h.Coordinator.Capture().Exclusive);
    }

    [Fact]
    public void PendingTransitionSurvivesExitAndClearsOnlyOnAcknowledgementOrTeardown()
    {
        var h = new Harness(); h.Join(h.First);
        var reader = Guid.NewGuid();
        h.Document.JoinEditingSession(reader, 3600, asEditor: false);
        Assert.True(h.Alone());
        h.Join(h.Second);
        Assert.False(h.Alone());
        Assert.True(h.Coordinator.Capture().CoauthorTransitionPending);
        h.Coauth(h.Second, "ExitCoauthoring", new());
        Assert.False(h.Alone());
        var marked = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Completed, h.Coauth(h.First, "MarkTransitionComplete", marked));
        Assert.Empty(marked.SubResponseDataAttributes);
        Assert.True(h.Alone());
        h.Join(h.First); // repeated join does not introduce another transition
        Assert.True(h.Alone());
        Assert.Equal(LockOperationResult.Completed, h.Coauth(h.First, "MarkTransitionComplete", new()));
        h.Coauth(h.First, "ExitCoauthoring", new());
        Assert.False(h.Alone());
        Assert.Equal(reader, Assert.Single(h.Document.Sessions).ClientId);
    }

    [Fact]
    public void AcknowledgementWithTwoMembersDoesNotMakeCallerAlone()
    {
        var h = new Harness(); h.Join(h.First); h.Join(h.Second);
        h.Coauth(h.First, "MarkTransitionComplete", new());
        Assert.False(h.Alone());
        Assert.False(h.Coordinator.Capture().CoauthorTransitionPending);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    public void FailedExclusiveConversionReleasesOnlyCallerWhenRequested(string release, bool leaves)
    {
        var h = new Harness(); h.Join(h.First); h.Join(h.Second);
        var reader = Guid.NewGuid(); h.Document.JoinEditingSession(reader, 3600, asEditor: false);
        var request = h.CoauthRequest(h.First, "ConvertToExclusive");
        request.SubRequestDataAttributes["ReleaseLockOnConversionToExclusiveFailure"] = release;
        var response = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Conflict, h.Coordinator.ApplyCoauthSession(h.Document, request, response));
        Assert.Equal(leaves ? "ExitCoauthSessionAsConvertToExclusiveFailed" : "MultipleClientsInCoauthSession", response.ErrorCode);
        Assert.Equal(leaves ? 1 : 2, h.Coordinator.Capture().CoauthorClients.Length);
        Assert.Equal(leaves ? 2 : 3, h.Document.Sessions.Count);
        Assert.NotNull(h.Document.GetSession(reader));
        Assert.NotNull(h.Document.GetSession(h.Second));
        Assert.Null(h.Coordinator.Capture().Exclusive);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    public void InvalidReleaseFlagDoesNotMutateState(string release)
    {
        var h = new Harness(); h.Join(h.First); h.Join(h.Second);
        var request = h.CoauthRequest(h.First, "ConvertToExclusive");
        request.SubRequestDataAttributes["ReleaseLockOnConversionToExclusiveFailure"] = release;
        string before = JsonSerializer.Serialize(h.Coordinator.Capture());
        Assert.Equal(LockOperationResult.InvalidArgument,
            h.Coordinator.ApplyCoauthSession(h.Document, request, new()));
        Assert.Equal(before, JsonSerializer.Serialize(h.Coordinator.Capture()));
        Assert.Equal(2, h.Document.Sessions.Count);
    }

    [Fact]
    public void SoleCoauthorConvertsWithoutRemovingUnrelatedReader()
    {
        var h = new Harness(); h.Join(h.First);
        var reader = Guid.NewGuid(); h.Document.JoinEditingSession(reader, 3600, asEditor: false);
        Assert.Equal(LockOperationResult.Completed, h.Coauth(h.First, "ConvertToExclusive", new()));
        var state = h.Coordinator.Capture();
        Assert.Empty(state.CoauthorClients); Assert.Empty(state.SchemaOwners);
        Assert.NotNull(state.Exclusive); Assert.False(state.CoauthorTransitionPending);
        Assert.Equal(reader, Assert.Single(h.Document.Sessions).ClientId);
    }

    [Fact]
    public void SchemaOnlyOwnerAlsoBlocksCoauthorConversion()
    {
        var h = new Harness(); h.Join(h.First);
        h.Coordinator.ApplySchemaLock(Harness.Request(("SchemaLockRequestType", "GetLock"),
            ("SchemaLockID", h.Schema), ("ClientID", h.Second.ToString())), new());
        var response = new FssHttpSubResponse(); h.Coauth(h.First, "ConvertToExclusive", response);
        Assert.Equal("MultipleClientsInCoauthSession", response.ErrorCode);
        Assert.NotNull(h.Document.GetSession(h.First));
    }

    [Fact]
    public void ExpiredCoauthorRefreshRejoinsAndDoesNotShortenAnActiveLease()
    {
        var h = new Harness(); var now = DateTime.UtcNow;
        var request = h.CoauthRequest(h.First, "JoinCoauthoring");
        request.SubRequestDataAttributes["Timeout"] = "3600";
        h.Coordinator.ApplyCoauthSession(h.Document, request, new(), now);
        var oldExpiry = Assert.Single(h.Coordinator.Capture().SchemaOwners).ExpiresUtc;
        request.SubRequestDataAttributes["CoauthRequestType"] = "RefreshCoauthoring";
        request.SubRequestDataAttributes["Timeout"] = "60";
        h.Coordinator.ApplyCoauthSession(h.Document, request, new(), now.AddSeconds(1));
        Assert.Equal(oldExpiry, Assert.Single(h.Coordinator.Capture().SchemaOwners).ExpiresUtc);
        var response = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Granted,
            h.Coordinator.ApplyCoauthSession(h.Document, request, response, now.AddHours(2)));
        Assert.Equal("Alone", response.SubResponseDataAttributes["CoauthStatus"]);
        Assert.Single(h.Coordinator.Capture().CoauthorClients);
    }

    [Fact]
    public void NonGuidCoauthorIdentityDoesNotInventAnEditorsTableKey()
    {
        var h = new Harness();
        var request = Harness.Request(("CoauthRequestType", "JoinCoauthoring"),
            ("ClientID", "native-client"), ("SchemaLockID", h.Schema));
        Assert.Equal(LockOperationResult.Granted, h.Coordinator.ApplyCoauthSession(h.Document, request, new()));
        Assert.Equal("native-client", Assert.Single(h.Coordinator.Capture().CoauthorClients));
        Assert.Empty(h.Document.Sessions);
        Assert.True(h.Alone());
    }

    [Fact]
    public void AnotherAuthenticatedSubjectCannotAcknowledgeOrConvertOwnersLease()
    {
        var h = new Harness(); h.Join(h.First);
        var before = JsonSerializer.Serialize(h.Coordinator.Capture());
        var other = new SubjectIdentity("local:other", "other", "Other");
        var coordinator = FssHttpLockCoordinator.For(h.Document, other);
        Assert.Equal(LockOperationResult.AccessDenied,
            coordinator.ApplyCoauthSession(h.Document, h.CoauthRequest(h.First, "MarkTransitionComplete"), new()));
        Assert.Equal(before, JsonSerializer.Serialize(coordinator.Capture()));
    }

    private sealed class Harness
    {
        public StoredDocument Document { get; } = new DocumentStore().GetOrAdd("/locks.docx");
        public FssHttpLockCoordinator Coordinator { get; }
        public string Schema { get; } = Guid.NewGuid().ToString();
        public string ExclusiveId { get; } = Guid.NewGuid().ToString();
        public Guid First { get; } = Guid.NewGuid();
        public Guid Second { get; } = Guid.NewGuid();
        public Harness() => Coordinator = FssHttpLockCoordinator.For(Document, TestActor.Value.Identity);
        public void Join(Guid client) => Assert.Contains(Coauth(client, "JoinCoauthoring", new()),
            new[] { LockOperationResult.Granted, LockOperationResult.Refreshed });
        public FssHttpSubRequest CoauthRequest(Guid client, string operation) => Request(
            ("CoauthRequestType", operation), ("ClientID", client.ToString()), ("SchemaLockID", Schema),
            ("ExclusiveLockID", ExclusiveId), ("Timeout", "3600"), ("ReleaseLockOnConversionToExclusiveFailure", "false"));
        public LockOperationResult Coauth(Guid client, string operation, FssHttpSubResponse response) =>
            Coordinator.ApplyCoauthSession(Document, CoauthRequest(client, operation), response);
        public FssHttpSubRequest ExclusiveRequest(string operation) => Request(("ExclusiveLockRequestType", operation),
            ("ExclusiveLockID", ExclusiveId), ("SchemaLockID", Schema), ("ClientID", First.ToString()), ("Timeout", "3600"));
        public LockOperationResult Exclusive(string operation, FssHttpSubResponse response) => Coordinator.ApplyExclusiveLock(ExclusiveRequest(operation), response);
        public bool Alone()
        {
            var response = new FssHttpSubResponse();
            Coordinator.ApplyAmIAlone(Document, Request(("TransitionID", Document.TransitionId.ToString())), response);
            return response.SubResponseDataAttributes["AmIAlone"] == "True";
        }
        public static FssHttpSubRequest Request(params (string Key, string Value)[] attributes)
        {
            var request = new FssHttpSubRequest();
            foreach (var (key, value) in attributes) request.SubRequestDataAttributes[key] = value;
            return request;
        }
    }
}
