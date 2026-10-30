using System.Security.Cryptography;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

internal static class AcknowledgedRevisionVerifier
{
    internal static async Task VerifyAsync(StorageProvider provider, IReadOnlyCollection<DocumentRevisionRecord> revisions,
        ulong initialLatestRevision, IReadOnlyDictionary<ExGuid, string> committed)
    {
        var acceptedRevisions = revisions.Where(revision => revision.RevisionNumber > initialLatestRevision).ToArray();
        if (acceptedRevisions.Length != committed.Count)
            throw new InvalidDataException("Acknowledged HTTP saves do not match the retained application revisions.");
        var remaining = committed.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var revision in acceptedRevisions)
        {
            if (!remaining.Remove(StorageIds.Restore(revision.Package.Root.StorageIndex), out var historicalExpectedHash))
                throw new InvalidDataException("A retained HTTP revision has a duplicate or unacknowledged storage index.");
            await using var historical = await provider.Packages.OpenReadAsync(revision.Package);
            var historicalHash = Convert.ToHexString(await SHA256.HashDataAsync(historical));
            if (!historicalHash.Equals(historicalExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An acknowledged historical HTTP revision differs from its expected package.");
        }
        if (remaining.Count != 0)
            throw new InvalidDataException("An acknowledged HTTP storage index has no verified revision.");
    }
}
