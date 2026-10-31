using System.Security.Claims;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed class ActorIdentityMetadataTests
{
    [Fact]
    public void FromPrincipalMapsOnlyTrustedEmailAndSipClaims()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(CellBridgeActor.SubjectClaim, "subject-1"),
            new Claim(ClaimTypes.Name, "person@domain.test"),
            new Claim(CellBridgeActor.DisplayNameClaim, "Person One"),
            new Claim(ClaimTypes.Email, "person@example.test"),
            new Claim(CellBridgeActor.SipAddressClaim, "sip:person@domain.test"),
        ], "test"));

        var actor = CellBridgeActor.FromPrincipal(principal)!;

        Assert.Equal("subject-1", actor.Identity.Subject);
        Assert.Equal("person@domain.test", actor.Identity.Login);
        Assert.Equal("Person One", actor.Identity.DisplayName);
        Assert.Equal("person@example.test", actor.Identity.EmailAddress);
        Assert.Equal("sip:person@domain.test", actor.Identity.SipAddress);
    }

    [Fact]
    public void FromPrincipalDoesNotTreatLoginAsEmailOrSipAddress()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(CellBridgeActor.SubjectClaim, "subject-1"),
            new Claim(ClaimTypes.Name, "person@domain.test"),
        ], "test"));

        var actor = CellBridgeActor.FromPrincipal(principal)!;

        Assert.Null(actor.Identity.EmailAddress);
        Assert.Null(actor.Identity.SipAddress);
    }

    [Fact]
    public void SubjectIdentityKeepsThreeArgumentAndLegacyJsonCompatibility()
    {
        var legacy = JsonSerializer.Deserialize<SubjectIdentity>(
            "{\"Subject\":\"subject-1\",\"Login\":\"person\",\"DisplayName\":\"Person\"}")!;

        Assert.Null(legacy.EmailAddress);
        Assert.Null(legacy.SipAddress);

        var current = new SubjectIdentity("subject-1", "person", "Person")
        {
            EmailAddress = "person@example.test",
            SipAddress = "sip:person@domain.test",
        };
        var restored = JsonSerializer.Deserialize<SubjectIdentity>(JsonSerializer.Serialize(current))!;
        Assert.Equal(current, restored);

        Assert.Equal("person", new SubjectIdentity("subject-1", "person", "Person").Login);
    }
}
