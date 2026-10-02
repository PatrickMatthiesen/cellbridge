namespace CellBridge.Storage;

using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CellBridge.FssHttpB;

/// <summary>
/// An in-memory document store. Documents are identified by URL and hold the
/// current file content plus a monotonically increasing content version.
/// </summary>
public sealed class DocumentStore
{
    private readonly Dictionary<string, StoredDocument> _documents =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attaches a detached document to a compatibility request context without changing it.</summary>
    public void Attach(StoredDocument document)
    {
        lock (_documents) _documents[document.Url] = document;
    }

    /// <summary>Adds or replaces a document.</summary>
    public StoredDocument Put(string url, byte[] content)
    {
        url = NormalizeUrl(url);
        StoredDocument? existing;
        lock (_documents)
        {
            if (!_documents.TryGetValue(url, out existing))
            {
                var doc = new StoredDocument(url);
                doc.SetContent(content);
                _documents[url] = doc;
                return doc;
            }
        }

        // Keep the dictionary lock short. The document lock still serializes
        // updates to this one resource, while unrelated documents can be
        // populated concurrently.
        existing.SetContent(content);
        return existing;
    }

    /// <summary>
    /// Adds a new document if the URL is unused. The existence check and
    /// insertion are one operation, so concurrent create requests cannot
    /// replace each other's documents.
    /// </summary>
    public bool TryAdd(string url, byte[] content, out StoredDocument document)
    {
        ArgumentNullException.ThrowIfNull(content);
        url = NormalizeUrl(url);
        lock (_documents)
        {
            if (_documents.TryGetValue(url, out document!))
            {
                return false;
            }

            document = new StoredDocument(url);
            document.SetContent(content);
            _documents.Add(url, document);
            return true;
        }
    }

    /// <summary>Gets a document by URL, or null.</summary>
    public StoredDocument? Get(string url)
    {
        url = NormalizeUrl(url);
        lock (_documents)
        {
            return _documents.TryGetValue(url, out var doc) ? doc : null;
        }
    }

    /// <summary>Gets a document or creates an empty one.</summary>
    public StoredDocument GetOrAdd(string url)
    {
        url = NormalizeUrl(url);
        lock (_documents)
        {
            if (!_documents.TryGetValue(url, out var doc))
            {
                doc = new StoredDocument(url);
                _documents[url] = doc;
            }

            return doc;
        }
    }

    /// <summary>Whether a document exists.</summary>
    public bool Exists(string url)
    {
        url = NormalizeUrl(url);
        lock (_documents)
        {
            return _documents.ContainsKey(url);
        }
    }

    /// <summary>
    /// Returns a point-in-time snapshot of the documents currently in the
    /// store. The returned array is independent from the store and remains
    /// unchanged when documents are added later.
    /// </summary>
    public StoredDocument[] List()
    {
        lock (_documents)
        {
            return _documents.Values.ToArray();
        }
    }

    /// <summary>
    /// Converts absolute SOAP URLs, escaped paths, and relative paths to the
    /// same document key used by the HTTP document endpoint.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        string path = Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.AbsolutePath
            : url.Split('?', 2)[0];

        path = Uri.UnescapeDataString(path).TrimEnd('/');
        return path.StartsWith('/') ? path : $"/{path}";
    }
}

/// <summary>
/// A stored document with versioned content and coauthoring state.
/// </summary>
public sealed partial class StoredDocument
{
    private byte[] _content = Array.Empty<byte>();
    private readonly List<CoauthSession> _sessions = new();

    /// <summary>The fixed SOAP partition identifier for Word file metadata.</summary>
    public static readonly Guid MetadataPartitionId =
        new("383ADC0B-E66E-4438-95E6-E39EF9720122");

    /// <summary>The fixed SOAP partition identifier for the editors table.</summary>
    public static readonly Guid EditorsTablePartitionId =
        new("7808F4DD-2385-49D6-B7CE-37ACA5E43602");

    internal StoredDocument(string url)
    {
        Url = url;
        FilePartition = new DocumentPartition(DocumentPartitionKind.FileContents);
        MetadataPartition = new DocumentPartition(DocumentPartitionKind.Metadata);
        EditorsTablePartition = new DocumentPartition(DocumentPartitionKind.EditorsTable);
        TransitionId = Guid.NewGuid();
        CreatedUtc = UtcNow;
        _lastModifiedUtc = CreatedUtc;
        UpdateMetadataPartition();
        UpdateEditorsTablePartition();
    }

    /// <summary>The canonical URL of the document.</summary>
    public string Url { get; }

    /// <summary>The current content version (increments on every save).</summary>
    public uint ContentVersion
    {
        get
        {
            lock (this)
            {
                return _contentVersion;
            }
        }
    }

    private uint _contentVersion;

    /// <summary>The default partition that contains the document bytes.</summary>
    public DocumentPartition FilePartition { get; }

    /// <summary>The Word metadata partition.</summary>
    public DocumentPartition MetadataPartition { get; }

    /// <summary>The Word editors-table partition.</summary>
    public DocumentPartition EditorsTablePartition { get; }

    /// <summary>Stable JoinCoauthoring transition identifier.</summary>
    public Guid TransitionId { get; private set; }

    /// <summary>
    /// Stable HTTP/FSSHTTP entity tag. SharePoint uses the document resource
    /// identifier, not a process-randomized URL hash.
    /// </summary>
    public string Etag
    {
        get
        {
            lock (this)
            {
                return $"\"{{{TransitionId.ToString("D").ToUpperInvariant()}}},{_contentVersion}\"";
            }
        }
    }

    /// <summary>The document creation time used by FSSHTTP file properties.</summary>
    public DateTime CreatedUtc { get; private set; }

    /// <summary>The last content update time used by FSSHTTP file properties.</summary>
    public DateTime LastModifiedUtc
    {
        get
        {
            lock (this)
            {
                return _lastModifiedUtc;
            }
        }
    }

    private DateTime _lastModifiedUtc;

    /// <summary>The current content.</summary>
    public byte[] Content
    {
        get
        {
            lock (this)
            {
                return _content.ToArray();
            }
        }
    }

    /// <summary>The current content length without copying the document bytes.</summary>
    public long ContentLength
    {
        get
        {
            lock (this)
            {
                return _metadataContentLength ?? _content.LongLength;
            }
        }
    }

    /// <summary>Returns the independently versioned state for a known partition.</summary>
    public DocumentPartition GetPartition(DocumentPartitionKind kind) => kind switch
    {
        DocumentPartitionKind.FileContents => FilePartition,
        DocumentPartitionKind.Metadata => MetadataPartition,
        DocumentPartitionKind.EditorsTable => EditorsTablePartition,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The active coauthoring sessions.</summary>
    public IReadOnlyList<CoauthSession> Sessions
    {
        get
        {
            lock (this)
            {
                RemoveExpiredSessionsLocked();
                return _sessions.Select(static session => session.Snapshot()).ToArray();
            }
        }
    }

    /// <summary>Replaces the content and bumps the version.</summary>
    public void SetContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (this)
        {
            _content = content.ToArray();
            _contentVersion = checked(_contentVersion + 1);
            _lastModifiedUtc = UtcNow;
            FilePartition.SetContent(_content);
            UpdateMetadataPartition();
        }
    }

    /// <summary>Joins a new coauthoring session for the given client.</summary>
    public CoauthSession JoinSession(Guid clientId, string? userName = null)
        => JoinEditingSession(clientId, CoauthSession.DefaultTimeoutSeconds, asEditor: true, userName: userName);

    /// <summary>
    /// Adds or refreshes an EditorsTable entry. Timeout is the SOAP timeout in
    /// seconds; the binary editors-table representation stores the resulting
    /// UTC expiration ticks.
    /// </summary>
    public CoauthSession JoinEditingSession(
        Guid clientId,
        int timeoutSeconds,
        bool asEditor,
        string? userName = null)
    {
        lock (this)
        {
            RemoveExpiredSessionsLocked();
            var existing = _sessions.FirstOrDefault(s => s.ClientId == clientId);
            if (existing is not null)
            {
                existing.AsEditor = asEditor;
                existing.TimeoutSeconds = timeoutSeconds;
                existing.Refresh(timeoutSeconds, UtcNow);
                if (!string.IsNullOrWhiteSpace(userName))
                {
                    existing.UserName = userName;
                }
                UpdateEditorsTablePartition();
                existing.LastSeenUtc = UtcNow;
                return existing.Snapshot();
            }

            var session = new CoauthSession(clientId, userName)
            {
                AsEditor = asEditor,
                TimeoutSeconds = timeoutSeconds,
                LastSeenUtc = UtcNow,
            };
            session.Refresh(timeoutSeconds, UtcNow);
            _sessions.Add(session);
            UpdateEditorsTablePartition();
            return session.Snapshot();
        }
    }

    /// <summary>Leaves the coauthoring session for the given client.</summary>
    public bool LeaveSession(Guid clientId)
    {
        lock (this)
        {
            RemoveExpiredSessionsLocked();
            var session = _sessions.FirstOrDefault(s => s.ClientId == clientId);
            if (session is null)
            {
                return false;
            }

            _sessions.Remove(session);
            UpdateEditorsTablePartition();
            return true;
        }
    }

    /// <summary>Gets the coauthoring session for a client, or null.</summary>
    public CoauthSession? GetSession(Guid clientId)
    {
        lock (this)
        {
            RemoveExpiredSessionsLocked();
            return _sessions.FirstOrDefault(s => s.ClientId == clientId)?.Snapshot();
        }
    }

    /// <summary>Commits a fully validated binary file revision under the document lock.</summary>
    public void CommitFileRevision(PartitionGraphSnapshot graph, byte[] content, ulong knowledgeSequence)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(content);
        lock (this)
        {
            _content = content.ToArray();
            _contentVersion = checked(_contentVersion + 1);
            _lastModifiedUtc = UtcNow;
            FilePartition.CommitGraph(graph, _content, knowledgeSequence);
            UpdateMetadataPartition();
        }
    }

    /// <summary>Refreshes an existing EditorsTable entry.</summary>
    public bool RefreshEditingSession(Guid clientId, int timeoutSeconds, bool asEditor)
    {
        lock (this)
        {
            RemoveExpiredSessionsLocked();
            var session = _sessions.FirstOrDefault(s => s.ClientId == clientId);
            if (session is null)
            {
                return false;
            }

            session.AsEditor = asEditor;
            session.TimeoutSeconds = timeoutSeconds;
            session.Refresh(timeoutSeconds, UtcNow);
            UpdateEditorsTablePartition();
            return true;
        }
    }

    /// <summary>Adds or replaces one metadata value for an editor.</summary>
    public bool UpdateEditorMetadata(Guid clientId, string key, byte[] value)
    {
        lock (this)
        {
            RemoveExpiredSessionsLocked();
            var session = _sessions.FirstOrDefault(s => s.ClientId == clientId);
            if (session is null)
            {
                return false;
            }

            session.Metadata[key] = value.ToArray();
            UpdateEditorsTablePartition();
            return true;
        }
    }

    /// <summary>Removes one metadata value for an editor.</summary>
    public bool RemoveEditorMetadata(Guid clientId, string key)
    {
        lock (this)
        {
            RemoveExpiredSessionsLocked();
            var session = _sessions.FirstOrDefault(s => s.ClientId == clientId);
            if (session is null)
            {
                return false;
            }

            bool removed = session.Metadata.Remove(key);
            if (removed)
            {
                UpdateEditorsTablePartition();
            }

            return true;
        }
    }

    private bool RemoveExpiredSessionsLocked()
    {
        var now = UtcNow;
        int removed = _sessions.RemoveAll(s => s.ExpiresUtc <= now);
        if (removed > 0)
        {
            UpdateEditorsTablePartition();
        }

        return removed > 0;
    }

    private void UpdateMetadataPartition()
    {
        // FSSHTTPB treats this as an application-specific partition. This
        // placeholder state deliberately is not the DOCX payload. Word's
        // exact metadata content will be captured from SharePoint separately.
        MetadataPartition.SetContent(Encoding.UTF8.GetBytes(
            $"<Metadata ContentVersion=\"{ContentVersion}\" Modified=\"{LastModifiedUtc.Ticks}\" />"));
    }

    private void UpdateEditorsTablePartition()
    {
        var editors = new XElement("EditorsTable");
        foreach (var session in _sessions)
        {
            var editor = new XElement("Editor",
                new XElement("CacheID", session.ClientId.ToString("D")));

            if (!string.IsNullOrWhiteSpace(session.UserName))
            {
                editor.Add(new XElement("FriendlyName", session.UserName));
                editor.Add(new XElement("LoginName", session.UserName));
            }

            editor.Add(new XElement("HasEditorPermission", session.AsEditor ? "true" : "false"));
            editor.Add(new XElement("Timeout", session.ExpiresUtc.Ticks.ToString(CultureInfo.InvariantCulture)));
            if (session.Metadata.Count > 0)
            {
                var metadata = new XElement("Metadata");
                foreach (var pair in session.Metadata)
                {
                    // Metadata keys are XML element names in MS-FSSHTTPB.
                    // Invalid names cannot be emitted as a schema-valid table.
                    if (!IsValidXmlName(pair.Key))
                    {
                        continue;
                    }

                    metadata.Add(new XElement(pair.Key, Convert.ToBase64String(pair.Value)));
                }

                if (metadata.HasElements)
                {
                    editor.Add(metadata);
                }
            }

            editors.Add(editor);
        }

        EditorsTablePartition.SetContent(CreateEditorsTableStream(
            editors.ToString(SaveOptions.DisableFormatting)));
    }

    /// <summary>
    /// Produces the FSSHTTPB editors-table stream: UTF-8 XML, DEFLATE, then
    /// the required eight-byte Editors Table Zip Stream Header. FSSHTTPD
    /// chunking remains to be added when the partition serializer is expanded.
    /// </summary>
    private static byte[] CreateEditorsTableStream(string editorsTableXml)
    {
        byte[] xml = Encoding.UTF8.GetBytes(editorsTableXml);
        using var compressed = new MemoryStream();
        using (var deflater = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(xml, 0, xml.Length);
        }

        byte[] header = [0x1A, 0x5A, 0x3A, 0x30, 0x00, 0x00, 0x00, 0x00];
        byte[] result = new byte[header.Length + compressed.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(compressed.GetBuffer(), 0, result, header.Length, (int)compressed.Length);
        return result;
    }

    private static bool IsValidXmlName(string name)
    {
        try
        {
            return XmlConvert.VerifyName(name) == name;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}

/// <summary>Known MS-FSSHTTP document partitions addressed by a SOAP Cell request.</summary>
public enum DocumentPartitionKind
{
    FileContents,
    Metadata,
    EditorsTable,
}

/// <summary>Independent FSSHTTPB state for one document partition.</summary>
public sealed partial class DocumentPartition
{
    private readonly object _gate = new();
    private PartitionGraphSnapshot? _graph;
    internal DocumentPartition(DocumentPartitionKind kind)
    {
        Kind = kind;
        ProtocolIdentity = DocumentStorageIdentity.Create();
    }

    public DocumentPartitionKind Kind { get; }
    public DocumentStorageIdentity ProtocolIdentity { get; private set; }
    public ulong KnowledgeSequence
    {
        get
        {
            lock (_gate)
            {
                return _knowledgeSequence;
            }
        }
    }

    public byte[] Content
    {
        get
        {
            lock (_gate)
            {
                return _content.ToArray();
            }
        }
    }

    private ulong _knowledgeSequence = 73507;
    private byte[] _content = Array.Empty<byte>();

    /// <summary>The file graph retained for subsequent delta uploads and downloads.</summary>
    public PartitionGraphSnapshot FileGraph
    {
        get
        {
            lock (_gate)
            {
                return _graph ??= CreateFileGraph();
            }
        }
    }

    private PartitionGraphSnapshot CreateFileGraph()
    {
        if (Kind != DocumentPartitionKind.FileContents)
            throw new InvalidOperationException("Only the file partition has a materialized document graph.");
        var response = FileContentPartitionBuilder.BuildQueryChangesResponse(
            1, _content, FssHttpBIdentity, _knowledgeSequence);
        var data = (QueryChangesSubResponseData)response.SubResponses[0].Data!;
        return PartitionGraphSnapshot.Create(response.DataElementPackage!.DataElements, data.StorageIndexExtendedGuid);
    }

    public StorageManifestBuilder.StableIdentity FssHttpBIdentity =>
        new(
            ProtocolIdentity.StorageManifestGuid,
            ProtocolIdentity.CellManifestGuid,
            ProtocolIdentity.RevisionManifestGuid,
            ProtocolIdentity.ObjectGroupGuid,
            ProtocolIdentity.ObjectDataBlobGuid,
            ProtocolIdentity.ObjectGuid,
            ProtocolIdentity.RevisionId,
            ProtocolIdentity.CellId,
            ProtocolIdentity.SerialGuid);

    internal void SetContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            _graph = null;
            _content = content.ToArray();
            _knowledgeSequence++;
        }
    }

    internal void CommitGraph(PartitionGraphSnapshot graph, byte[] content, ulong knowledgeSequence)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            _content = content.ToArray();
            _knowledgeSequence = knowledgeSequence;
            _graph = graph;
        }
    }
}

/// <summary>
/// A coauthoring session: one Word instance editing the document.
/// </summary>
public sealed partial class CoauthSession
{
    public const int DefaultTimeoutSeconds = 3600;

    internal CoauthSession(Guid clientId, string? userName)
    {
        ClientId = clientId;
        UserName = userName;
        JoinedUtc = DateTime.UtcNow;
        TimeoutSeconds = DefaultTimeoutSeconds;
        ExpiresUtc = JoinedUtc.AddSeconds(DefaultTimeoutSeconds);
    }

    private CoauthSession(CoauthSession source)
    {
        ClientId = source.ClientId;
        UserName = source.UserName;
        JoinedUtc = source.JoinedUtc;
        TimeoutSeconds = source.TimeoutSeconds;
        ExpiresUtc = source.ExpiresUtc;
        LastSeenUtc = source.LastSeenUtc;
        AsEditor = source.AsEditor;
        EditorNumber = source.EditorNumber;
        foreach (var pair in source.Metadata)
        {
            Metadata[pair.Key] = pair.Value.ToArray();
        }
    }

    /// <summary>The client ID (the CoauthID Word provides).</summary>
    public Guid ClientId { get; }

    /// <summary>The user name, if known.</summary>
    public string? UserName { get; set; }

    /// <summary>Whether the client joined with editor permission.</summary>
    public bool AsEditor { get; internal set; }

    /// <summary>SOAP timeout requested for this entry, in seconds.</summary>
    public int TimeoutSeconds { get; internal set; }

    /// <summary>UTC time at which this entry expires.</summary>
    public DateTime ExpiresUtc { get; private set; }

    /// <summary>Arbitrary editor metadata values, limited by the SOAP layer.</summary>
    public Dictionary<string, byte[]> Metadata { get; } = new(StringComparer.Ordinal);

    /// <summary>When the session joined.</summary>
    public DateTime JoinedUtc { get; }

    /// <summary>Last activity time for the session.</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>The editor table entry number (1-based) for this session.</summary>
    public int EditorNumber { get; set; }

    internal void Refresh(int timeoutSeconds, DateTime? now = null)
    {
        TimeoutSeconds = timeoutSeconds;
        LastSeenUtc = now ?? DateTime.UtcNow;
        ExpiresUtc = LastSeenUtc.AddSeconds(timeoutSeconds);
    }

    /// <summary>Returns a detached view safe for callers to inspect.</summary>
    internal CoauthSession Snapshot() => new(this);
}

/// <summary>Stable protocol identifiers for one stored document.</summary>
public sealed partial class DocumentStorageIdentity
{
    private DocumentStorageIdentity(
        ExGuid storageManifestGuid,
        ExGuid cellManifestGuid,
        ExGuid revisionManifestGuid,
        ExGuid objectGroupGuid,
        ExGuid objectDataBlobGuid,
        ExGuid objectGuid,
        ExGuid revisionId,
        CellId cellId,
        Guid serialGuid)
    {
        StorageManifestGuid = storageManifestGuid;
        CellManifestGuid = cellManifestGuid;
        RevisionManifestGuid = revisionManifestGuid;
        ObjectGroupGuid = objectGroupGuid;
        ObjectDataBlobGuid = objectDataBlobGuid;
        ObjectGuid = objectGuid;
        RevisionId = revisionId;
        CellId = cellId;
        SerialGuid = serialGuid;
    }

    public ExGuid StorageManifestGuid { get; }
    public ExGuid CellManifestGuid { get; }
    public ExGuid RevisionManifestGuid { get; }
    public ExGuid ObjectGroupGuid { get; }
    public ExGuid ObjectDataBlobGuid { get; }
    public ExGuid ObjectGuid { get; }
    public ExGuid RevisionId { get; }
    public CellId CellId { get; }
    public Guid SerialGuid { get; }

    public static DocumentStorageIdentity Create()
    {
        var serialGuid = Guid.NewGuid();
        return new DocumentStorageIdentity(
            new ExGuid(1, Guid.NewGuid()),
            new ExGuid(2, Guid.NewGuid()),
            new ExGuid(3, Guid.NewGuid()),
            new ExGuid(4, Guid.NewGuid()),
            new ExGuid(5, Guid.NewGuid()),
            new ExGuid(6, Guid.NewGuid()),
            new ExGuid(7, Guid.NewGuid()),
            new CellId(
                new ExGuid(1, StorageManifestBuilder.RootExtendedGuid),
                new ExGuid(1, StorageManifestBuilder.CellSecondExtendedGuid)),
            serialGuid);
    }
}
