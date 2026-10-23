#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

internal sealed class SchemaVersionRow { public bool Singleton { get; set; } = true; public int Version { get; set; } }
internal sealed class TemplateRow { public Guid TemplateId { get; set; } public long Version { get; set; } public string Definition { get; set; } = null!; }
internal sealed class WorkspaceRow
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = null!;
    public string AuthorizationMode { get; set; } = "builtin";
    public string? AuthorizationDomain { get; set; }
    public string? AuthorizationScope { get; set; }
    public long StoredBytes { get; set; }
    public long AuditCount { get; set; }
    public int SecurityAuditCount { get; set; }
}
internal sealed class MembershipRow { public Guid WorkspaceId { get; set; } public string Subject { get; set; } = null!; public bool CanCreateLists { get; set; } public bool CanManageGrants { get; set; } }
internal sealed class ListRow
{
    public Guid WorkspaceId { get; set; }
    public Guid ListId { get; set; }
    public string Name { get; set; } = null!;
    public long SchemaVersion { get; set; }
    public Guid? TemplateId { get; set; }
    public long? TemplateVersion { get; set; }
    public string? NumberPrefix { get; set; }
    public long NextNumber { get; set; } = 1;
}
internal sealed class ListSchemaRow { public Guid WorkspaceId { get; set; } public Guid ListId { get; set; } public long SchemaVersion { get; set; } public string Definition { get; set; } = null!; }
internal sealed class ListCounterRow { public Guid WorkspaceId { get; set; } public Guid ListId { get; set; } public long IdentityCount { get; set; } public long NextNumber { get; set; } = 1; }
internal sealed class ListGrantRow { public Guid WorkspaceId { get; set; } public Guid ListId { get; set; } public string Subject { get; set; } = null!; public int Access { get; set; } }
internal sealed class RecordRow
{
    public Guid WorkspaceId { get; set; }
    public Guid ListId { get; set; }
    public Guid RecordId { get; set; }
    public long SchemaVersion { get; set; }
    public long Revision { get; set; }
    public long Number { get; set; }
    public string ValuesJson { get; set; } = null!;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
    public string CreatedBy { get; set; } = null!;
    public string ModifiedBy { get; set; } = null!;
}
internal sealed class RecordTombstoneRow { public Guid WorkspaceId { get; set; } public Guid ListId { get; set; } public Guid RecordId { get; set; } }
internal sealed class CreateReceiptRow
{
    public Guid WorkspaceId { get; set; }
    public Guid ListId { get; set; }
    public Guid RecordId { get; set; }
    public string Actor { get; set; } = null!;
    public long SchemaVersion { get; set; }
    public string RequestDigest { get; set; } = null!;
    public long Revision { get; set; }
    public long Number { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
}
internal sealed class RecordReferenceRow
{
    public Guid WorkspaceId { get; set; }
    public Guid SourceListId { get; set; }
    public Guid SourceRecordId { get; set; }
    public Guid FieldId { get; set; }
    public Guid TargetListId { get; set; }
    public Guid TargetRecordId { get; set; }
}
internal sealed class SavedViewRow { public Guid WorkspaceId { get; set; } public Guid ListId { get; set; } public Guid ViewId { get; set; } public string Definition { get; set; } = null!; }
internal sealed class AuditEventRow
{
    public long Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public DateTimeOffset Utc { get; set; }
    public string Actor { get; set; } = null!;
    public string Action { get; set; } = null!;
    public Guid? ListId { get; set; }
    public Guid? RecordId { get; set; }
    public long? Revision { get; set; }
    public Guid[] FieldIds { get; set; } = [];
    public string? TargetSubject { get; set; }
    public bool Security { get; set; }
}
