using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

public sealed partial class StoredDocument
{
    private StoredDocument(DocumentControlRead control)
    {
        var head = control.Head;
        Url = head.Path;
        TransitionId = head.ResourceId;
        CreatedUtc = head.CreatedUtc;
        _lastModifiedUtc = head.ModifiedUtc;
        _contentVersion = head.ContentVersion;
        Security = head.Security;
        _metadataContentLength = head.Package.Length;
        FilePartition = new DocumentPartition(control.Partitions.Single(p => p.Kind == 0));
        MetadataPartition = new DocumentPartition(control.Partitions.Single(p => p.Kind == 1));
        EditorsTablePartition = new DocumentPartition(control.Partitions.Single(p => p.Kind == 2));
        _sessions.AddRange(control.Editors.Select(CoauthSession.Restore));
        UseAuthoritativeTime(control.UtcNow);
    }

    /// <summary>Creates a detached current-control view without reading graph or history records.</summary>
    public static StoredDocument RestoreControl(DocumentControlRead control) => new(control);

    internal DocumentControlRead CaptureControl(DocumentControlRead current, CoordinationState coordination)
    {
        _ = Sessions;
        return current with
        {
            Editors = _sessions.Select(s => s.Capture()).ToImmutableArray(),
            Coordination = coordination,
            Partitions = current.Partitions.Select(p => p.Kind == 2 ? p with
            {
                Knowledge = EditorsTablePartition.KnowledgeSequence,
                InlineContent = EditorsTablePartition.Content.ToImmutableArray(),
            } : p).ToImmutableArray(),
        };
    }
}

public sealed partial class DocumentPartition
{
    internal DocumentPartition(PartitionHead head)
    {
        Kind = (DocumentPartitionKind)head.Kind;
        ProtocolIdentity = DocumentStorageIdentity.Restore(head.Identity);
        _knowledgeSequence = head.Knowledge;
        _content = head.InlineContent.ToArray();
    }
}
