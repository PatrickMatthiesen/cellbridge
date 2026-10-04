using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Conformance;

/// <summary>Provider author checks. Run against a disposable backend: published test content is retained.</summary>
public static class ProviderConformance
{
    public static async Task VerifyAsync(StorageProvider provider, CancellationToken cancellationToken = default)
    {
        await provider.State.CheckHealthAsync(cancellationToken);
        byte[] bytes = [0, 127, 255, 4];
        using var source = new MemoryStream(bytes, writable: false);
        var handle = await provider.Content.WriteAsync(source, cancellationToken);
        var restored = await provider.Content.ReadVerifiedAsync(handle, bytes.Length, cancellationToken);
        Require(bytes.SequenceEqual(restored), "Content round trip changed bytes.");
        var id = Guid.NewGuid();
        var path = "/conformance/" + id.ToString("N");
        var now = DateTime.UtcNow;
        var initial = new DocumentState(DocumentState.CurrentFormat, id, path, path.ToUpperInvariant(), now, now,
            1, 0, handle, [], [], CoordinationState.Empty, []);
        Require(await provider.State.TryCreateAsync(initial, cancellationToken), "Creation failed.");
        Require(!await provider.State.TryCreateAsync(initial with { ResourceId = Guid.NewGuid() }, cancellationToken), "Duplicate path was accepted.");
        Require(!await provider.State.TryCreateAsync(initial with { Path = path + "2", PathKey = path.ToUpperInvariant() + "2" }, cancellationToken), "Duplicate resource ID was accepted.");
        var snapshot = await provider.State.FindByResourceIdAsync(id, cancellationToken);
        Require(snapshot is not null && snapshot.ResourceId == id, "Resource lookup failed.");
        Require((await provider.State.FindByPathKeyAsync(initial.PathKey, cancellationToken))?.ResourceId == id, "Path lookup failed.");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.State.TransitionAsync(id, (current, clock) =>
            new StateTransition<bool>(current with
            {
                ContentVersion = checked(current.ContentVersion + 1),
                Coordination = new(null, [], new("conformance-lease", "client", clock.AddMinutes(1), 0, null),
                    checked(current.Coordination.Generation + 1)),
            }, true), cancellationToken).AsTask()));
        var final = (await provider.State.FindByResourceIdAsync(id, cancellationToken))!;
        Require(final.ContentVersion == 9 && final.StateVersion == 8 && final.Coordination.Generation == 8, "Concurrent transitions lost updates.");
        Require(final.Coordination.Exclusive?.Id == "conformance-lease", "Lease state did not persist.");
        Require(snapshot!.ContentVersion == 1 && snapshot.Coordination.Exclusive is null, "An acquired snapshot was mutated.");
        try
        {
            await provider.State.TransitionAsync<bool>(id, (_, _) => throw new ConformanceAbortException(), cancellationToken);
            throw new InvalidOperationException("A callback failure was swallowed.");
        }
        catch (ConformanceAbortException) { }
        Require((await provider.State.FindByResourceIdAsync(id, cancellationToken))!.StateVersion == final.StateVersion,
            "A failed transition published state.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class ConformanceAbortException : Exception;
}
