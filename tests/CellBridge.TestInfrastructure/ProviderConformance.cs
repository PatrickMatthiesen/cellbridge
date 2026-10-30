using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.TestInfrastructure;

/// <summary>Provider author checks. Run against a disposable backend: published test content is retained.</summary>
public static class ProviderConformance
{
    public static async Task VerifyAsync(StorageProvider provider, CancellationToken cancellationToken = default)
    {
        await provider.Documents.CheckHealthAsync(cancellationToken);
        byte[] bytes = [0, 127, 255, 4];
        using var source = new MemoryStream(bytes, writable: false);
        var handle = await provider.Content.WriteAsync(source, cancellationToken);
        var restored = await provider.Content.ReadVerifiedAsync(handle, bytes.Length, cancellationToken);
        Require(bytes.SequenceEqual(restored), "Content round trip changed bytes.");
        var actor = new SubjectIdentity("conformance", "conformance", "Conformance");
        var id = Guid.NewGuid();
        var path = "/conformance/" + id.ToString("N");
        var security = DocumentSecurity.Create(actor);
        var initial = await IndexedDocumentPublicationBuilder.CreateAsync(id, path, bytes, security, provider.Content, provider.Limits, cancellationToken);
        Require((await provider.Documents.CreateAsync(initial, cancellationToken)).Created, "Creation failed.");
        var duplicatePath = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), path, bytes, security, provider.Content, provider.Limits, cancellationToken);
        Require(!(await provider.Documents.CreateAsync(duplicatePath, cancellationToken)).Created, "Duplicate path was accepted.");
        var duplicateId = await IndexedDocumentPublicationBuilder.CreateAsync(id, path + "2", bytes, security, provider.Content, provider.Limits, cancellationToken);
        Require(!(await provider.Documents.CreateAsync(duplicateId, cancellationToken)).Created, "Duplicate resource ID was accepted.");
        var snapshot = await provider.Documents.ReadControlAsync(id, cancellationToken);
        Require(snapshot is not null && snapshot.Head.ResourceId == id, "Resource lookup failed.");
        Require((await provider.Documents.FindAsync(new DocumentIdentity.Path(initial.Head.PathKey), cancellationToken))?.ResourceId == id, "Path lookup failed.");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var current = await provider.Documents.ReadControlAsync(id, cancellationToken) ?? throw new InvalidOperationException("Conformance document disappeared.");
                var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(id,
                    Admission(current.Head), security, current.Head.AuthorityGeneration), cancellationToken);
                if (result.Changed) return;
                if (result.Current.Head.StateVersion == current.Head.StateVersion)
                    throw new InvalidOperationException("A current conformance permission command failed: " + result.ErrorCode);
            }
            throw new InvalidOperationException("Conformance control retries did not converge.");
        }));
        var final = (await provider.Documents.ReadControlAsync(id, cancellationToken))!;
        Require(final.Head.ContentVersion == 1 && final.Head.StateVersion == 8 && final.Head.AuthorityGeneration == 8 &&
            final.Coordination.Generation == 8, "Concurrent typed controls lost updates.");
        var lease = new HostLease("conformance-lease", actor.Subject, final.UtcNow.AddMinutes(1));
        var locked = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetHostLease(id, Admission(final.Head), lease, null), cancellationToken);
        Require(locked.Changed && locked.Current.Coordination.HostLock == lease, "Lease state did not persist.");
        Require(snapshot!.Head.StateVersion == 0 && snapshot.Coordination.HostLock is null, "An acquired snapshot was mutated.");
        var stale = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(id,
            Admission(snapshot.Head), security, locked.Current.Head.AuthorityGeneration), cancellationToken);
        Require(!stale.Changed && stale.ErrorCode is not null, "A stale authority admission changed the document.");
        var invalid = new PreparedDocumentPublication(id, Admission(locked.Current.Head),
            new(1, locked.Current.Head.AuthorityGeneration, locked.Current.Coordination.Generation,
                locked.Current.Head.StateVersion, locked.Current.Head.ContentVersion, []),
            new(new(1, SaveOperationFamily.Protocol, 0, "invalid"), "", actor.Subject, 1, 1, null, null), []);
        try
        {
            await provider.Documents.PublishAsync(invalid, cancellationToken);
            throw new InvalidOperationException("An invalid typed publication was accepted.");
        }
        catch (InvalidDataException) { }
        Require((await provider.Documents.ReadControlAsync(id, cancellationToken))!.Head.StateVersion == locked.Current.Head.StateVersion,
            "A rejected typed publication changed the document.");

        DocumentActorAdmission Admission(DocumentHead head) => new(actor, id, head.LifecycleGeneration, head.Security.AuthorizationPolicy,
            DocumentAccess.Read | DocumentAccess.Write) { StateVersion = head.StateVersion, AuthorityGeneration = head.AuthorityGeneration };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
