using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

/// <summary>Provider-owned namespace reservations; released names never bind a different resource.</summary>
public static class DocumentPathReservations
{
    public static void ValidateTransition(DocumentState current, DocumentState next, bool rename)
    {
        DocumentLifecycle.ValidateTransition(current, rename
            ? next with { Path = current.Path, PathKey = current.PathKey } : next);
        if (next.PathKey != next.Path.ToUpperInvariant())
            throw new InvalidOperationException("A document path key must be canonical.");
    }

    public static void ValidateCreate(DocumentState state)
    {
        if (!state.RetiredPathKeys.IsEmpty)
            throw new InvalidOperationException("New documents cannot reserve retired paths.");
    }

    public static ImmutableArray<string> Capture(DocumentState current, DocumentState next, bool rename)
    {
        if (!current.RetiredPathKeys.SequenceEqual(next.RetiredPathKeys))
            throw new InvalidOperationException("A transition cannot change provider-owned path reservations.");
        return rename && next.PathKey != current.PathKey && !current.RetiredPathKeys.Contains(current.PathKey)
            ? current.RetiredPathKeys.Add(current.PathKey) : current.RetiredPathKeys;
    }
}
