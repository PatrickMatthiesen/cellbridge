using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using System.Reflection;

namespace CellBridge.Lists.PostgreSql;

/// <summary>
/// Owns the CellBridge Lists PostgreSQL model. Consumers configure the EF provider but do not extend this context.
/// </summary>
public sealed class PostgreSqlListDbContext(DbContextOptions<PostgreSqlListDbContext> options) : DbContext(options)
{
    internal static string? JsonText(string json, string path) => throw new NotSupportedException();
    internal static byte[]? Utf16Key(string? value) => throw new NotSupportedException();
    internal static string? TimestampKey(string? value) => throw new NotSupportedException();
    internal static int Utf16Compare(string? left, string? right) => throw new NotSupportedException();
    internal static int TimestampCompare(string? left, string? right) => throw new NotSupportedException();
    internal static int CCompare(string? left, string? right) => throw new NotSupportedException();
    internal static DateTimeOffset ClockTimestamp() => throw new NotSupportedException();

    internal DbSet<SchemaVersionRow> SchemaVersions => Set<SchemaVersionRow>();
    internal DbSet<TemplateRow> Templates => Set<TemplateRow>();
    internal DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();
    internal DbSet<MembershipRow> Memberships => Set<MembershipRow>();
    internal DbSet<ListRow> Lists => Set<ListRow>();
    internal DbSet<ListSchemaRow> ListSchemas => Set<ListSchemaRow>();
    internal DbSet<ListCounterRow> ListCounters => Set<ListCounterRow>();
    internal DbSet<ListGrantRow> ListGrants => Set<ListGrantRow>();
    internal DbSet<RecordRow> Records => Set<RecordRow>();
    internal DbSet<RecordTombstoneRow> RecordTombstones => Set<RecordTombstoneRow>();
    internal DbSet<CreateReceiptRow> CreateReceipts => Set<CreateReceiptRow>();
    internal DbSet<RecordReferenceRow> RecordReferences => Set<RecordReferenceRow>();
    internal DbSet<SavedViewRow> SavedViews => Set<SavedViewRow>();
    internal DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("cellbridge_lists");
        Configure(modelBuilder.Entity<SchemaVersionRow>(), "schema_version", [nameof(SchemaVersionRow.Singleton)]);
        Configure(modelBuilder.Entity<TemplateRow>(), "templates", [nameof(TemplateRow.TemplateId), nameof(TemplateRow.Version)]);
        Configure(modelBuilder.Entity<WorkspaceRow>(), "workspaces", [nameof(WorkspaceRow.WorkspaceId)]);
        Configure(modelBuilder.Entity<MembershipRow>(), "memberships", [nameof(MembershipRow.WorkspaceId), nameof(MembershipRow.Subject)]);
        Configure(modelBuilder.Entity<ListRow>(), "lists", [nameof(ListRow.WorkspaceId), nameof(ListRow.ListId)]);
        Configure(modelBuilder.Entity<ListSchemaRow>(), "list_schemas", [nameof(ListSchemaRow.WorkspaceId), nameof(ListSchemaRow.ListId), nameof(ListSchemaRow.SchemaVersion)]);
        Configure(modelBuilder.Entity<ListCounterRow>(), "list_counters", [nameof(ListCounterRow.WorkspaceId), nameof(ListCounterRow.ListId)]);
        Configure(modelBuilder.Entity<ListGrantRow>(), "list_grants", [nameof(ListGrantRow.WorkspaceId), nameof(ListGrantRow.ListId), nameof(ListGrantRow.Subject)]);
        Configure(modelBuilder.Entity<RecordRow>(), "records", [nameof(RecordRow.WorkspaceId), nameof(RecordRow.ListId), nameof(RecordRow.RecordId)]);
        Configure(modelBuilder.Entity<RecordTombstoneRow>(), "record_tombstones", [nameof(RecordTombstoneRow.WorkspaceId), nameof(RecordTombstoneRow.ListId), nameof(RecordTombstoneRow.RecordId)]);
        Configure(modelBuilder.Entity<CreateReceiptRow>(), "create_receipts", [nameof(CreateReceiptRow.WorkspaceId), nameof(CreateReceiptRow.ListId), nameof(CreateReceiptRow.RecordId)]);
        Configure(modelBuilder.Entity<RecordReferenceRow>(), "record_references", [nameof(RecordReferenceRow.WorkspaceId), nameof(RecordReferenceRow.SourceListId), nameof(RecordReferenceRow.SourceRecordId), nameof(RecordReferenceRow.FieldId)]);
        Configure(modelBuilder.Entity<SavedViewRow>(), "saved_views", [nameof(SavedViewRow.WorkspaceId), nameof(SavedViewRow.ListId), nameof(SavedViewRow.ViewId)]);
        Configure(modelBuilder.Entity<AuditEventRow>(), "audit_events", [nameof(AuditEventRow.WorkspaceId), nameof(AuditEventRow.Id)]);

        modelBuilder.Entity<SchemaVersionRow>().Property(x => x.Singleton).HasDefaultValue(true);
        modelBuilder.Entity<SchemaVersionRow>().ToTable(table => table.HasCheckConstraint(
            "schema_version_singleton_check", "singleton"));
        modelBuilder.Entity<TemplateRow>().ToTable(table => table.HasCheckConstraint(
            "templates_version_check", "version > 0"));
        modelBuilder.Entity<TemplateRow>().Property(x => x.Definition).HasColumnType("jsonb");
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.AuthorizationMode).HasDefaultValue("builtin");
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.AuthorizationDomain).UseCollation("C");
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.AuthorizationScope).UseCollation("C");
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.StoredBytes).HasDefaultValue(0L);
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.AuditCount).HasDefaultValue(0L);
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.SecurityAuditCount).HasDefaultValue(0);
        modelBuilder.Entity<WorkspaceRow>().ToTable(table =>
        {
            table.HasCheckConstraint("workspaces_authorization_anchor_check", "(authorization_mode='builtin' AND authorization_domain IS NULL AND authorization_scope IS NULL) OR (authorization_mode='host' AND authorization_domain IS NOT NULL AND authorization_scope IS NOT NULL AND char_length(authorization_domain) BETWEEN 1 AND 200 AND btrim(authorization_domain)<>'' AND char_length(authorization_scope) BETWEEN 1 AND 1024 AND btrim(authorization_scope)<>'')");
            table.HasCheckConstraint("workspaces_stored_bytes_check", "stored_bytes >= 0");
            table.HasCheckConstraint("workspaces_audit_count_check", "audit_count >= 0");
            table.HasCheckConstraint("workspaces_security_audit_count_check", "security_audit_count >= 0");
        });
        modelBuilder.Entity<MembershipRow>().Property(x => x.CanCreateLists).HasDefaultValue(false);
        modelBuilder.Entity<MembershipRow>().Property(x => x.CanManageGrants).HasDefaultValue(false);
        modelBuilder.Entity<ListRow>().Property(x => x.NextNumber).HasDefaultValue(1L);
        modelBuilder.Entity<ListRow>().ToTable(table =>
        {
            table.HasCheckConstraint("lists_schema_version_check", "schema_version > 0");
            table.HasCheckConstraint("lists_next_number_check", "next_number > 0");
        });
        modelBuilder.Entity<ListSchemaRow>().ToTable(table => table.HasCheckConstraint(
            "list_schemas_schema_version_check", "schema_version > 0"));
        modelBuilder.Entity<ListSchemaRow>().Property(x => x.Definition).HasColumnType("jsonb");
        modelBuilder.Entity<ListCounterRow>().Property(x => x.IdentityCount).HasDefaultValue(0L);
        modelBuilder.Entity<ListCounterRow>().Property(x => x.NextNumber).HasDefaultValue(1L);
        modelBuilder.Entity<ListCounterRow>().ToTable(table =>
        {
            table.HasCheckConstraint("list_counters_identity_count_check", "identity_count >= 0");
            table.HasCheckConstraint("list_counters_next_number_check", "next_number > 0");
        });
        modelBuilder.Entity<ListGrantRow>().ToTable(table => table.HasCheckConstraint(
            "list_grants_access_check", "access >= 0 AND access <= 7"));
        modelBuilder.Entity<RecordRow>().ToTable(table =>
        {
            table.HasCheckConstraint("records_revision_check", "revision > 0");
            table.HasCheckConstraint("records_number_check", "number > 0");
        });
        modelBuilder.Entity<RecordRow>().Property(x => x.ValuesJson).HasColumnType("jsonb");
        modelBuilder.Entity<SavedViewRow>().Property(x => x.Definition).HasColumnType("jsonb");
        modelBuilder.Entity<AuditEventRow>().Property(x => x.Id).UseIdentityAlwaysColumn();
        modelBuilder.Entity<AuditEventRow>().Property(x => x.FieldIds).HasColumnType("uuid[]")
            .HasDefaultValueSql("'{}'::uuid[]");
        modelBuilder.Entity<AuditEventRow>().Property(x => x.Security).HasDefaultValue(false);
        modelBuilder.Entity<RecordRow>().HasAlternateKey(x => new { x.WorkspaceId, x.ListId, x.Number })
            .HasName("records_workspace_id_list_id_number_key");
        modelBuilder.Entity<RecordReferenceRow>().HasIndex(x => new { x.WorkspaceId, x.TargetListId, x.TargetRecordId })
            .HasDatabaseName("record_references_by_target");

        modelBuilder.Entity<MembershipRow>().HasOne<WorkspaceRow>().WithMany()
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("memberships_workspace_id_fkey");
        modelBuilder.Entity<ListRow>().HasOne<WorkspaceRow>().WithMany()
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("lists_workspace_id_fkey");
        modelBuilder.Entity<ListRow>().HasOne<TemplateRow>().WithMany()
            .HasForeignKey(x => new { x.TemplateId, x.TemplateVersion }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("lists_template_id_template_version_fkey");
        modelBuilder.Entity<ListSchemaRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("list_schemas_workspace_id_list_id_fkey");
        modelBuilder.Entity<ListCounterRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("list_counters_workspace_id_list_id_fkey");
        modelBuilder.Entity<ListGrantRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("list_grants_workspace_id_list_id_fkey");
        modelBuilder.Entity<ListGrantRow>().HasOne<MembershipRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.Subject }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("list_grants_workspace_id_subject_fkey");
        modelBuilder.Entity<RecordRow>().HasOne<ListSchemaRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId, x.SchemaVersion }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("records_workspace_id_list_id_schema_version_fkey");
        modelBuilder.Entity<RecordTombstoneRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("record_tombstones_workspace_id_list_id_fkey");
        modelBuilder.Entity<CreateReceiptRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("create_receipts_workspace_id_list_id_fkey");
        modelBuilder.Entity<RecordReferenceRow>().HasOne<RecordRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.SourceListId, x.SourceRecordId }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("record_references_source_fk");
        modelBuilder.Entity<RecordReferenceRow>().HasOne<RecordRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.TargetListId, x.TargetRecordId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("record_references_target_fk");
        modelBuilder.Entity<SavedViewRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("saved_views_workspace_id_list_id_fkey");
        modelBuilder.Entity<AuditEventRow>().HasOne<WorkspaceRow>().WithMany()
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("audit_events_workspace_id_fkey");
        MapFunction(modelBuilder, nameof(JsonText), "jsonb_extract_path_text", schema: null, builtIn: true);
        MapFunction(modelBuilder, nameof(Utf16Key), "utf16_key");
        MapFunction(modelBuilder, nameof(TimestampKey), "timestamp_key");
        MapFunction(modelBuilder, nameof(Utf16Compare), "utf16_compare");
        MapFunction(modelBuilder, nameof(TimestampCompare), "timestamp_compare");
        MapFunction(modelBuilder, nameof(CCompare), "c_compare");
        MapFunction(modelBuilder, nameof(ClockTimestamp), "clock_timestamp", schema: null, builtIn: true);
    }

    private static void Configure<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string[] key)
        where TEntity : class
    {
        entity.ToTable(table);
        entity.HasKey(key).HasName(table + "_pkey");
        foreach (var property in entity.Metadata.GetProperties())
            property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static void MapFunction(ModelBuilder modelBuilder, string methodName, string databaseName,
        string? schema = "cellbridge_lists", bool builtIn = false)
    {
        var method = typeof(PostgreSqlListDbContext).GetMethod(methodName,
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException();
        var function = modelBuilder.HasDbFunction(method).HasName(databaseName);
        if (schema is not null) function.HasSchema(schema);
        if (builtIn) function.IsBuiltIn();
    }

    private static string ToSnakeCase(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
