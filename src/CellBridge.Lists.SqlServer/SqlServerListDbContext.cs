using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text;

namespace CellBridge.Lists.SqlServer;

/// <summary>
/// Owns the CellBridge Lists SQL Server model. Consumers configure the EF provider but do not extend this context.
/// </summary>
public sealed class SqlServerListDbContext(DbContextOptions<SqlServerListDbContext> options) : DbContext(options)
{
    public const string Schema = "cellbridge_lists";
    public const string MigrationsHistoryTable = "__CellBridgeListsMigrationsHistory";

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
        modelBuilder.HasDefaultSchema(Schema);
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
        modelBuilder.Entity<SchemaVersionRow>().HasData(new SchemaVersionRow { Singleton = true, Version = 1 });
        modelBuilder.Entity<SchemaVersionRow>().ToTable(table => table.HasCheckConstraint(
            "schema_version_singleton_check", "singleton = 1"));
        modelBuilder.Entity<TemplateRow>().ToTable(table => table.HasCheckConstraint(
            "templates_version_check", "version > 0"));
        modelBuilder.Entity<TemplateRow>().Property(x => x.Definition).HasColumnType("nvarchar(max)");
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.AuthorizationMode).HasDefaultValue("builtin");
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.AuthorizationDomain).UseCollation("Latin1_General_100_BIN2");
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.AuthorizationScope).UseCollation("Latin1_General_100_BIN2");
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.StoredBytes).HasDefaultValue(0L);
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.AuditCount).HasDefaultValue(0L);
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.SecurityAuditCount).HasDefaultValue(0);
        modelBuilder.Entity<WorkspaceRow>().ToTable(table =>
        {
            table.HasTrigger("immutable_workspace_authorization_anchor");
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("workspaces_authorization_anchor_check", "(authorization_mode='builtin' AND authorization_domain IS NULL AND authorization_scope IS NULL) OR (authorization_mode='host' AND authorization_domain IS NOT NULL AND authorization_scope IS NOT NULL AND DATALENGTH(authorization_domain)/2 BETWEEN 1 AND 200 AND TRIM(authorization_domain)<>'' AND DATALENGTH(authorization_scope)/2 BETWEEN 1 AND 1024 AND TRIM(authorization_scope)<>'')");
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
        modelBuilder.Entity<ListSchemaRow>().Property(x => x.Definition).HasColumnType("nvarchar(max)");
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
        modelBuilder.Entity<RecordRow>().Property(x => x.ValuesJson).HasColumnType("nvarchar(max)");
        modelBuilder.Entity<SavedViewRow>().Property(x => x.Definition).HasColumnType("nvarchar(max)");
        modelBuilder.Entity<AuditEventRow>().Property(x => x.Id).UseIdentityColumn();
        modelBuilder.Entity<AuditEventRow>().Property(x => x.FieldIds)
            .HasConversion(value => System.Text.Json.JsonSerializer.Serialize(value, (System.Text.Json.JsonSerializerOptions?)null),
                value => System.Text.Json.JsonSerializer.Deserialize<Guid[]>(value, (System.Text.Json.JsonSerializerOptions?)null)!);
        modelBuilder.Entity<AuditEventRow>().Property(x => x.Security).HasDefaultValue(false);
        modelBuilder.Entity<RecordRow>().HasAlternateKey(x => new { x.WorkspaceId, x.ListId, x.Number })
            .HasName("records_workspace_id_list_id_number_key");
        modelBuilder.Entity<RecordReferenceRow>().HasIndex(x => new { x.WorkspaceId, x.TargetListId, x.TargetRecordId })
            .HasDatabaseName("record_references_by_target");

        modelBuilder.Entity<MembershipRow>().HasOne<WorkspaceRow>().WithMany()
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("memberships_workspace_id_fkey");
        modelBuilder.Entity<ListRow>().HasOne<WorkspaceRow>().WithMany()
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("lists_workspace_id_fkey");
        modelBuilder.Entity<ListRow>().HasOne<TemplateRow>().WithMany()
            .HasForeignKey(x => new { x.TemplateId, x.TemplateVersion }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("lists_template_id_template_version_fkey");
        modelBuilder.Entity<ListSchemaRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("list_schemas_workspace_id_list_id_fkey");
        modelBuilder.Entity<ListCounterRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("list_counters_workspace_id_list_id_fkey");
        modelBuilder.Entity<ListGrantRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("list_grants_workspace_id_list_id_fkey");
        ConfigureSubject(modelBuilder.Entity<MembershipRow>());
        ConfigureSubject(modelBuilder.Entity<ListGrantRow>());
        modelBuilder.Entity<ListGrantRow>().HasOne<MembershipRow>().WithMany()
            .HasForeignKey("WorkspaceId", "SubjectKey").OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<RecordRow>().HasOne<ListSchemaRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId, x.SchemaVersion }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("records_workspace_id_list_id_schema_version_fkey");
        modelBuilder.Entity<RecordTombstoneRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("record_tombstones_workspace_id_list_id_fkey");
        modelBuilder.Entity<CreateReceiptRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("create_receipts_workspace_id_list_id_fkey");
        modelBuilder.Entity<RecordReferenceRow>().HasOne<RecordRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.SourceListId, x.SourceRecordId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("record_references_source_fk");
        modelBuilder.Entity<RecordReferenceRow>().HasOne<RecordRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.TargetListId, x.TargetRecordId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("record_references_target_fk");
        modelBuilder.Entity<SavedViewRow>().HasOne<ListRow>().WithMany()
            .HasForeignKey(x => new { x.WorkspaceId, x.ListId }).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("saved_views_workspace_id_list_id_fkey");
        modelBuilder.Entity<AuditEventRow>().HasOne<WorkspaceRow>().WithMany()
            .HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("audit_events_workspace_id_fkey");
        foreach (var (type, property) in new[] {
            (typeof(TemplateRow), "Definition"), (typeof(ListSchemaRow), "Definition"),
            (typeof(RecordRow), "ValuesJson"), (typeof(SavedViewRow), "Definition"),
            (typeof(AuditEventRow), "FieldIds") })
        {
            var entity = modelBuilder.Entity(type);
            var column = ToSnakeCase(property);
            entity.ToTable(table => table.HasCheckConstraint(entity.Metadata.GetTableName() + "_" + column + "_json",
                "ISJSON([" + column + "]) = 1"));
        }
        modelBuilder.Entity<WorkspaceRow>().Property(x => x.WorkspaceId).ValueGeneratedNever();
        modelBuilder.Entity<RecordRow>().Property(x => x.Revision).IsConcurrencyToken();
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.ClrType == typeof(string) && property.GetValueConverter()?.ProviderClrType != typeof(byte[]))
                    property.SetCollation("Latin1_General_100_BIN2");
    }

    private static void ConfigureSubject<TEntity>(EntityTypeBuilder<TEntity> entity) where TEntity : class
    {
        var encoding = new ValueConverter<string, byte[]>(
            value => Encoding.Unicode.GetBytes(value), value => Encoding.Unicode.GetString(value));
        entity.Property<string>("Subject").HasConversion(encoding).HasColumnType("varbinary(max)").IsRequired();
        entity.ToTable(table => table.HasCheckConstraint(typeof(TEntity) == typeof(MembershipRow)
            ? "memberships_subject_length" : "list_grants_subject_length", "DATALENGTH(subject) BETWEEN 2 AND 2048"));
        var keyProperty = entity.Property<byte[]>("SubjectKey").HasColumnName("subject_key")
            .HasColumnType("binary(32)")
            .HasComputedColumnSql("ISNULL(CONVERT(binary(32), HASHBYTES('SHA2_256', [subject])), CONVERT(binary(32), 0x))", stored: true)
            .ValueGeneratedOnAdd();
        keyProperty.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        var key = typeof(TEntity) == typeof(MembershipRow)
            ? new[] { "WorkspaceId", "SubjectKey" } : new[] { "WorkspaceId", "ListId", "SubjectKey" };
        entity.HasKey(key);
        // Replacing the convention key can make its former Subject member optional.
        entity.Property<string>("Subject").IsRequired();
    }

    private static void Configure<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string[] key)
        where TEntity : class
    {
        entity.ToTable(table);
        entity.HasKey(key).HasName(table + "_pkey");
        foreach (var property in entity.Metadata.GetProperties())
            property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string value) => string.Concat(value.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
