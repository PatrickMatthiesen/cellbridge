using System.Text.Json;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class LockInputValidationTests
{
    [Theory]
    [InlineData("RefreshCoauthoring")]
    [InlineData("ConvertToExclusive")]
    [InlineData("MarkTransitionComplete")]
    public async Task CoauthOwnerDenialPrecedesMalformedScalarsForStringClientIds(string operation)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/coauth-owner.docx", MinimalDocx.Create(), TestActor.Value))!;
        var other = new CellBridgeActor(new("tests:other", "other", "Other user"));
        await provider.State.TransitionAsync(state.ResourceId, (s, _) => new StateTransition<bool>(s with
        { Security = s.Security with { Grants = s.Security.Grants.SetItem(other.Identity.Subject, DocumentAccess.Read | DocumentAccess.Write) } }, true));
        var processor = new CellBridgeRequestProcessor(service);
        var initial = Attributes(SubRequestType.Coauth, "JoinCoauthoring"); initial["ClientID"] = "string-client";
        var joined = await processor.ExecuteAsync(CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.Coauth, initial)), "https://host.test", TestActor.Value);
        Assert.Equal("Success", Assert.Single(Assert.Single(joined.Response.Responses).SubResponses).ErrorCode);
        var before = JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId));
        var attrs = Attributes(SubRequestType.Coauth, operation); attrs["ClientID"] = initial["ClientID"];
        var doc = new DocumentStore().GetOrAdd("/direct-coauth-owner.docx");
        var now = DateTime.UtcNow; doc.UseAuthoritativeTime(now);
        var coordinator = FssHttpLockCoordinator.For(doc, TestActor.Value.Identity);
        Assert.Equal(LockOperationResult.Granted, Apply(coordinator, doc, SubRequestType.Coauth, initial, now));
        var snapshot = JsonSerializer.Serialize(coordinator.Capture()); FssHttpLockCoordinator.For(doc, other.Identity);
        foreach (string? timeout in new string?[] { null, "", "bad" })
        {
            if (timeout is null) attrs.Remove("Timeout"); else attrs["Timeout"] = timeout;
            var result = await processor.ExecuteAsync(CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.Coauth, attrs)), "https://host.test", other);
            Assert.Equal("FileUnauthorizedAccess", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
            Assert.Equal(before, JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId)));
            Assert.Equal(LockOperationResult.AccessDenied, Apply(coordinator, doc, SubRequestType.Coauth, attrs, now));
            if (operation != "RefreshCoauthoring")
            {
                var transition = new FssHttpSubRequest { Type = SubRequestType.Coauth };
                foreach (var (key, value) in attrs) transition.SubRequestDataAttributes[key] = value;
                Assert.Equal(LockOperationResult.AccessDenied, coordinator.ApplyCoauthTransition(
                    transition, new(), now));
            }
            Assert.Equal(snapshot, JsonSerializer.Serialize(coordinator.Capture()));
        }
    }

    [Theory]
    [InlineData(SubRequestType.SchemaLock)]
    [InlineData(SubRequestType.ExclusiveLock)]
    public async Task ActiveOwnerDenialPrecedesMalformedTimeout(SubRequestType type)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/owner-input.docx", MinimalDocx.Create(), TestActor.Value))!;
        var other = new CellBridgeActor(new("tests:other", "other", "Other user"));
        await provider.State.TransitionAsync(state.ResourceId, (s, _) => new StateTransition<bool>(s with
        { Security = s.Security with { Grants = s.Security.Grants.SetItem(other.Identity.Subject, DocumentAccess.Read | DocumentAccess.Write) } }, true));
        var processor = new CellBridgeRequestProcessor(service);
        var attrs = Attributes(type, "GetLock");
        await processor.ExecuteAsync(CellStorageRequestParser.Parse(Soap(state.ResourceId, type, attrs)), "https://host.test", TestActor.Value);
        var before = JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId));
        attrs[type == SubRequestType.SchemaLock ? "SchemaLockRequestType" : "ExclusiveLockRequestType"] = "RefreshLock";
        foreach (string? timeout in new string?[] { null, "", "bad" })
        {
            if (timeout is null) attrs.Remove("Timeout"); else attrs["Timeout"] = timeout;
            var result = await processor.ExecuteAsync(CellStorageRequestParser.Parse(Soap(state.ResourceId, type, attrs)), "https://host.test", other);
            Assert.Equal("FileUnauthorizedAccess", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
            Assert.Equal(before, JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId)));
        }
        var doc = new DocumentStore().GetOrAdd("/direct-owner.docx");
        var now = DateTime.UtcNow; var coordinator = FssHttpLockCoordinator.For(doc, TestActor.Value.Identity);
        Assert.Equal(LockOperationResult.Granted, Apply(coordinator, doc, type, Attributes(type, "GetLock"), now));
        var snapshot = JsonSerializer.Serialize(coordinator.Capture());
        FssHttpLockCoordinator.For(doc, other.Identity);
        Assert.Equal(LockOperationResult.AccessDenied, Apply(coordinator, doc, type, attrs, now));
        Assert.Equal(snapshot, JsonSerializer.Serialize(coordinator.Capture()));
    }

    [Fact]
    public async Task RefreshIgnoresJoinFallbackRequirementBeforeAndAfterExpiry()
    {
        var doc = new DocumentStore().GetOrAdd("/refresh-fallback.docx");
        var now = DateTime.UtcNow; doc.UseAuthoritativeTime(now);
        var coordinator = FssHttpLockCoordinator.For(doc, TestActor.Value.Identity);
        var attrs = Attributes(SubRequestType.Coauth, "RefreshCoauthoring");
        attrs["AllowFallbackToExclusive"] = "true";
        attrs.Remove("ExclusiveLockID");
        foreach (var instant in new[] { now, now.AddDays(2) })
        {
            doc.UseAuthoritativeTime(instant);
            Assert.Equal(LockOperationResult.Granted, Apply(coordinator, doc, SubRequestType.Coauth, attrs, instant));
            Assert.Equal(instant.AddSeconds(3600), Assert.Single(coordinator.Capture().SchemaOwners).ExpiresUtc);
        }
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/refresh-processor.docx", MinimalDocx.Create(), TestActor.Value))!;
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(
            CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.Coauth, attrs)), "https://host.test", TestActor.Value);
        Assert.Equal("Success", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
        MicrosoftResponseSchema.Validate(result.Response.ToSoapEnvelope());
    }

    public static TheoryData<SubRequestType, string> TimedOperations => new()
    {
        { SubRequestType.SchemaLock, "GetLock" }, { SubRequestType.SchemaLock, "RefreshLock" },
        { SubRequestType.SchemaLock, "ConvertToExclusive" },
        { SubRequestType.Coauth, "JoinCoauthoring" }, { SubRequestType.Coauth, "RefreshCoauthoring" },
        { SubRequestType.Coauth, "ConvertToExclusive" },
        { SubRequestType.ExclusiveLock, "GetLock" }, { SubRequestType.ExclusiveLock, "RefreshLock" },
        { SubRequestType.ExclusiveLock, "ConvertToSchema" },
        { SubRequestType.ExclusiveLock, "ConvertToSchemaJoinCoauth" },
    };

    [Theory]
    [MemberData(nameof(TimedOperations))]
    public async Task MissingOrInvalidTimeoutReturnsSubresponseErrorWithoutPublishing(SubRequestType type, string operation)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/timeout.docx", MinimalDocx.Create(), TestActor.Value))!;
        var processor = new CellBridgeRequestProcessor(service);
        foreach (string? timeout in new string?[] { null, "", " \t\r\n", "59", "120001", "9223372036854775808", "abc", "60.0", "6e1", "6 0", "\u00a060\u00a0" })
        {
            var attrs = Attributes(type, operation);
            if (timeout is null) attrs.Remove("Timeout"); else attrs["Timeout"] = timeout;
            // Parsing preserves the operation's optional union. Missing required
            // operation input must become InvalidArgument, not a transport error.
            var request = CellStorageRequestParser.Parse(Soap(state.ResourceId, type, attrs));
            var result = await processor.ExecuteAsync(request, "https://host.test", TestActor.Value);
            var file = Assert.Single(result.Response.Responses);
            Assert.Null(file.ErrorCode);
            Assert.Equal("InvalidArgument", Assert.Single(file.SubResponses).ErrorCode);
            MicrosoftResponseSchema.Validate(result.Response.ToSoapEnvelope());
            Assert.Empty(result.AcceptedSaves);
            Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
        }
    }

    [Theory]
    [InlineData(SubRequestType.SchemaLock, "GetLock", " +60 ", 3600)]
    [InlineData(SubRequestType.SchemaLock, "RefreshLock", "3599", 3600)]
    [InlineData(SubRequestType.SchemaLock, "GetLock", "3600", 3600)]
    [InlineData(SubRequestType.SchemaLock, "GetLock", "120000", 120000)]
    [InlineData(SubRequestType.Coauth, "JoinCoauthoring", "60", 3600)]
    [InlineData(SubRequestType.Coauth, "RefreshCoauthoring", "120000", 120000)]
    [InlineData(SubRequestType.SchemaLock, "ConvertToExclusive", "60", 3600)]
    [InlineData(SubRequestType.Coauth, "ConvertToExclusive", "60", 3600)]
    [InlineData(SubRequestType.ExclusiveLock, "GetLock", "60", 60)]
    [InlineData(SubRequestType.ExclusiveLock, "RefreshLock", "120000", 120000)]
    [InlineData(SubRequestType.ExclusiveLock, "ConvertToSchema", "60", 60)]
    [InlineData(SubRequestType.ExclusiveLock, "ConvertToSchemaJoinCoauth", "60", 60)]
    public void TimeoutPolicyPreservesNormativeBoundaries(SubRequestType type, string operation, string timeout, int seconds)
    {
        var document = new DocumentStore().GetOrAdd("/duration.docx");
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        document.UseAuthoritativeTime(now);
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        if (operation != "GetLock" && operation != "JoinCoauthoring")
        {
            string initial = type == SubRequestType.ExclusiveLock ? "GetLock" :
                type == SubRequestType.Coauth ? "JoinCoauthoring" : "GetLock";
            Assert.Equal(LockOperationResult.Granted, Apply(coordinator, document, type, Attributes(type, initial), now));
        }
        var attrs = Attributes(type, operation); attrs["Timeout"] = timeout;
        Assert.Contains(Apply(coordinator, document, type, attrs, now),
            new[] { LockOperationResult.Granted, LockOperationResult.Refreshed, LockOperationResult.Completed });
        var state = coordinator.Capture();
        var lease = state.Exclusive ?? Assert.Single(state.SchemaOwners);
        Assert.Equal(now.AddSeconds(seconds), lease.ExpiresUtc);
    }

    [Theory]
    [InlineData(SubRequestType.SchemaLock, "GetLock")]
    [InlineData(SubRequestType.Coauth, "JoinCoauthoring")]
    public async Task FallbackFlagRequiresExclusiveIdAndValidBoolean(SubRequestType type, string operation)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/fallback.docx", MinimalDocx.Create(), TestActor.Value))!;
        var processor = new CellBridgeRequestProcessor(service);
        foreach (var text in new[] { "true", "1", " true ", "TRUE", "", "yes" })
        {
            var attrs = Attributes(type, operation); attrs.Remove("ExclusiveLockID");
            attrs["AllowFallbackToExclusive"] = text;
            var result = await processor.ExecuteAsync(CellStorageRequestParser.Parse(Soap(state.ResourceId, type, attrs)),
                "https://host.test", TestActor.Value);
            Assert.Equal("InvalidArgument", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
            Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
        }
        foreach (var text in new string?[] { null, "false", "0", " false " })
        {
            var attrs = Attributes(type, operation); attrs.Remove("ExclusiveLockID");
            if (text is not null) attrs["AllowFallbackToExclusive"] = text;
            var result = await processor.ExecuteAsync(CellStorageRequestParser.Parse(Soap(state.ResourceId, type, attrs)),
                "https://host.test", TestActor.Value);
            Assert.Equal("Success", Assert.Single(Assert.Single(result.Response.Responses).SubResponses).ErrorCode);
            MicrosoftResponseSchema.Validate(result.Response.ToSoapEnvelope());
        }
    }

    [Theory]
    [InlineData(SubRequestType.SchemaLock, "CheckLockAvailability", "ClientID")]
    [InlineData(SubRequestType.Coauth, "GetCoauthoringStatus", "SchemaLockID")]
    [InlineData(SubRequestType.ExclusiveLock, "ReleaseLock", "ExclusiveLockID")]
    public void RequiredIdentifiersPrecedeLockConflictWithoutExpiringState(SubRequestType type, string operation, string missing)
    {
        var doc = new DocumentStore().GetOrAdd("/required.docx");
        var coordinator = FssHttpLockCoordinator.For(doc, TestActor.Value.Identity);
        var now = DateTime.UtcNow;
        Assert.Equal(LockOperationResult.Granted, Apply(coordinator, doc, SubRequestType.ExclusiveLock,
            Attributes(SubRequestType.ExclusiveLock, "GetLock"), now));
        string before = JsonSerializer.Serialize(coordinator.Capture());
        var attrs = Attributes(type, operation); attrs.Remove(missing);
        Assert.Equal(LockOperationResult.InvalidArgument, Apply(coordinator, doc, type, attrs, now.AddDays(2)));
        Assert.Equal(before, JsonSerializer.Serialize(coordinator.Capture()));
    }

    [Fact]
    public async Task RejectedCoauthInputDoesNotPublishExpiredEditorsOrKnowledge()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/expired.docx", MinimalDocx.Create(), TestActor.Value))!;
        var processor = new CellBridgeRequestProcessor(service);
        var join = Attributes(SubRequestType.Coauth, "JoinCoauthoring");
        await processor.ExecuteAsync(CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.Coauth, join)),
            "https://host.test", TestActor.Value);
        state = await provider.State.TransitionAsync(state.ResourceId, (current, _) =>
        {
            var expired = DateTime.UtcNow.AddDays(-1);
            var next = current with
            {
                Editors = [Assert.Single(current.Editors) with { ExpiresUtc = expired }],
                Coordination = current.Coordination with
                { SchemaOwners = [Assert.Single(current.Coordination.SchemaOwners) with { ExpiresUtc = expired }] },
            };
            return new StateTransition<DocumentState>(next, next);
        });
        state = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        string before = JsonSerializer.Serialize(state);
        join.Remove("Timeout");
        var invalid = await processor.ExecuteAsync(CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.Coauth, join)),
            "https://host.test", TestActor.Value);
        Assert.Equal("InvalidArgument", Assert.Single(Assert.Single(invalid.Response.Responses).SubResponses).ErrorCode);
        Assert.Equal(before, JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId)));

        var valid = CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.SchemaLock,
            Attributes(SubRequestType.SchemaLock, "CheckLockAvailability")));
        Assert.Equal("Success", Assert.Single(Assert.Single((await processor.ExecuteAsync(valid,
            "https://host.test", TestActor.Value)).Response.Responses).SubResponses).ErrorCode);
        var after = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Empty(after.Editors); Assert.Empty(after.Coordination.SchemaOwners);
        Assert.True(after.Partitions.Single(p => p.Kind == 2).Knowledge > state.Partitions.Single(p => p.Kind == 2).Knowledge);
    }

    [Theory]
    [InlineData(SubRequestType.SchemaLock, "GetLock")]
    [InlineData(SubRequestType.ExclusiveLock, "GetLock")]
    [InlineData(SubRequestType.Coauth, "JoinCoauthoring")]
    public void MalformedTimeoutPrecedesActiveConflictButValidInputRetainsConflict(SubRequestType type, string operation)
    {
        var doc = new DocumentStore().GetOrAdd("/conflict.docx");
        var coordinator = FssHttpLockCoordinator.For(doc, TestActor.Value.Identity);
        var now = DateTime.UtcNow;
        var active = Attributes(SubRequestType.ExclusiveLock, "GetLock"); active["ExclusiveLockID"] = "other-lock";
        Assert.Equal(LockOperationResult.Granted, Apply(coordinator, doc, SubRequestType.ExclusiveLock, active, now));
        string before = JsonSerializer.Serialize(coordinator.Capture());
        var attrs = Attributes(type, operation); attrs["Timeout"] = "59";
        Assert.Equal(LockOperationResult.InvalidArgument, Apply(coordinator, doc, type, attrs, now));
        attrs["Timeout"] = "60";
        Assert.Equal(LockOperationResult.Conflict, Apply(coordinator, doc, type, attrs, now));
        Assert.Equal(before, JsonSerializer.Serialize(coordinator.Capture()));
    }

    [Theory]
    [InlineData(SubRequestType.SchemaLock)]
    [InlineData(SubRequestType.Coauth)]
    public void ConversionFlagsDistinguishMissingInvalidAndValidValues(SubRequestType type)
    {
        var doc = new DocumentStore().GetOrAdd("/flags.docx");
        var now = DateTime.UtcNow;
        var coordinator = FssHttpLockCoordinator.For(doc, TestActor.Value.Identity);
        var attrs = Attributes(type, "ConvertToExclusive");
        foreach (var text in new string?[] { null, "", " ", "TRUE", "2", "\u00a0false\u00a0" })
        {
            if (text is null) attrs.Remove("ReleaseLockOnConversionToExclusiveFailure");
            else attrs["ReleaseLockOnConversionToExclusiveFailure"] = text;
            Assert.Equal(LockOperationResult.InvalidArgument, Apply(coordinator, doc, type, attrs, now));
        }
        var initial = Attributes(type, type == SubRequestType.Coauth ? "JoinCoauthoring" : "GetLock");
        initial["ReleaseLockOnConversionToExclusiveFailure"] = "false";
        Assert.Equal(LockOperationResult.InvalidArgument, Apply(coordinator, doc, type, initial, now));
        initial.Remove("ReleaseLockOnConversionToExclusiveFailure");
        Assert.Equal(LockOperationResult.Granted, Apply(coordinator, doc, type, initial, now));
        attrs["ReleaseLockOnConversionToExclusiveFailure"] = " 0 ";
        Assert.Equal(LockOperationResult.Completed, Apply(coordinator, doc, type, attrs, now));
    }

    [Theory]
    [InlineData(SubRequestType.SchemaLock, "ReleaseLock")]
    [InlineData(SubRequestType.SchemaLock, "CheckLockAvailability")]
    [InlineData(SubRequestType.Coauth, "ExitCoauthoring")]
    [InlineData(SubRequestType.Coauth, "GetCoauthoringStatus")]
    [InlineData(SubRequestType.Coauth, "CheckLockAvailability")]
    [InlineData(SubRequestType.Coauth, "MarkTransitionComplete")]
    [InlineData(SubRequestType.ExclusiveLock, "ReleaseLock")]
    [InlineData(SubRequestType.ExclusiveLock, "CheckLockAvailability")]
    public void OtherOperationsPermitOmittedTimeout(SubRequestType type, string operation)
    {
        var doc = new DocumentStore().GetOrAdd("/optional.docx");
        var coordinator = FssHttpLockCoordinator.For(doc, TestActor.Value.Identity);
        var now = DateTime.UtcNow;
        Assert.Equal(LockOperationResult.Granted, Apply(coordinator, doc, type,
            Attributes(type, type == SubRequestType.Coauth ? "JoinCoauthoring" : "GetLock"), now));
        var attrs = Attributes(type, operation); attrs.Remove("Timeout"); attrs.Remove("ExclusiveLockID");
        if (type == SubRequestType.ExclusiveLock) attrs["ExclusiveLockID"] = "exclusive-token";
        Assert.Contains(Apply(coordinator, doc, type, attrs, now),
            new[] { LockOperationResult.Released, LockOperationResult.Observed, LockOperationResult.Completed });
    }

    [Fact]
    public async Task InvalidLockInputCanDriveOnFailDependencyWithoutStoppingIndependentOperations()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/batch.docx", MinimalDocx.Create(), TestActor.Value))!;
        var attrs = Attributes(SubRequestType.SchemaLock, "GetLock"); attrs.Remove("Timeout");
        var request = CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.SchemaLock, attrs));
        request.Requests[0].SubRequests.Add(new()
        { Type = SubRequestType.ServerTime, SubRequestToken = 2, DependsOn = 1, DependencyType = "OnFail" });
        request.Requests[0].SubRequests.Add(new()
        { Type = SubRequestType.WhoAmI, SubRequestToken = 3 });
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(request, "https://host.test", TestActor.Value);
        Assert.Equal(new[] { "InvalidArgument", "Success", "Success" },
            Assert.Single(result.Response.Responses).SubResponses.Select(r => r.ErrorCode));
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    [Fact]
    public async Task MicrosoftSchemaRejectsMissingRequiredResponseFieldsAndInventedErrors()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/schema-control.docx", MinimalDocx.Create(), TestActor.Value))!;
        var attrs = Attributes(SubRequestType.ExclusiveLock, "GetLock"); attrs.Remove("Timeout");
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(
            CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.ExclusiveLock, attrs)), "https://host.test", TestActor.Value);
        string soap = result.Response.ToSoapEnvelope();
        MicrosoftResponseSchema.Validate(soap);
        foreach (string field in new[] { "HResult", "ErrorCode", "SubRequestToken" })
        {
            var xml = XDocument.Parse(soap);
            xml.Descendants().Single(e => e.Name.LocalName == "SubResponse").Attribute(field)!.Remove();
            Assert.NotEmpty(MicrosoftResponseSchema.Errors(xml.ToString()));
        }
        var invalid = XDocument.Parse(soap);
        invalid.Descendants().Single(e => e.Name.LocalName == "SubResponse").SetAttributeValue("ErrorCode", "InventedError");
        Assert.NotEmpty(MicrosoftResponseSchema.Errors(invalid.ToString()));
    }

    [Fact]
    public async Task AuthorizationAndDependenciesPrecedeOperationInputErrors()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/precedence.docx", MinimalDocx.Create(), TestActor.Value))!;
        var processor = new CellBridgeRequestProcessor(service);
        var attrs = Attributes(SubRequestType.ExclusiveLock, "GetLock"); attrs.Remove("Timeout");
        var request = CellStorageRequestParser.Parse(Soap(state.ResourceId, SubRequestType.ExclusiveLock, attrs));
        var denied = await processor.ExecuteAsync(request, "https://host.test",
            new CellBridgeActor(new SubjectIdentity("other", "other", "Other")));
        Assert.Equal("FileUnauthorizedAccess", Assert.Single(Assert.Single(denied.Response.Responses).SubResponses).ErrorCode);
        request.Requests[0].SubRequests[0].DependsOn = 9;
        request.Requests[0].SubRequests[0].DependencyType = "OnSuccess";
        var dependency = await processor.ExecuteAsync(request, "https://host.test", TestActor.Value);
        Assert.Equal("DependentRequestNotExecuted", Assert.Single(Assert.Single(dependency.Response.Responses).SubResponses).ErrorCode);
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    internal static Dictionary<string, string> Attributes(SubRequestType type, string operation)
    {
        var result = new Dictionary<string, string>
        {
        [type + "RequestType"] = operation, ["ClientID"] = "11111111-1111-1111-1111-111111111111",
        ["SchemaLockID"] = "shared-schema", ["ExclusiveLockID"] = "exclusive-token", ["Timeout"] = "3600",
        };
        if (operation == "ConvertToExclusive") result["ReleaseLockOnConversionToExclusiveFailure"] = "false";
        return result;
    }

    internal static string Soap(Guid id, SubRequestType type, Dictionary<string, string> attributes) =>
        $"<Envelope><Body><RequestVersion Version=\"2\" MinorVersion=\"3\"/><RequestCollection CorrelationId=\"{Guid.NewGuid()}\">" +
        $"<Request Url=\"https://host.test/locks.docx\" RequestToken=\"1\" UseResourceID=\"true\" ResourceID=\"{id}\">" +
        $"<SubRequest Type=\"{type}\" SubRequestToken=\"1\">" +
        new XElement("SubRequestData", attributes.Select(a => new XAttribute(a.Key, a.Value))) +
        "</SubRequest></Request></RequestCollection></Body></Envelope>";

    private static LockOperationResult Apply(FssHttpLockCoordinator coordinator, StoredDocument document,
        SubRequestType type, Dictionary<string, string> attrs, DateTime now)
    {
        var request = new FssHttpSubRequest(); foreach (var attr in attrs) request.SubRequestDataAttributes.Add(attr.Key, attr.Value);
        var response = new FssHttpSubResponse();
        return type switch
        {
            SubRequestType.SchemaLock => coordinator.ApplySchemaLock(request, response, now),
            SubRequestType.Coauth => coordinator.ApplyCoauthSession(document, request, response, now),
            _ => coordinator.ApplyExclusiveLock(request, response, now),
        };
    }

    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());
}
