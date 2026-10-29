using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CellBridge.Storage.Shared;

internal static class RepositoryModel
{
    internal static readonly (Type Type, string Table)[] Tables =
    [
        (typeof(RepositoryHeadRow), "heads"), (typeof(RepositoryCoordinationRow), "coordination"),
        (typeof(RepositoryEditorRow), "editors"), (typeof(RepositoryPartitionRow), "partitions"),
        (typeof(RepositoryElementRow), "elements"), (typeof(RepositoryVisibilityRow), "visibility"),
        (typeof(RepositoryGraphRow), "graph"), (typeof(RepositoryBindingRow), "bindings"),
        (typeof(RepositoryBindingChangeRow), "binding_changes"), (typeof(RepositoryCheckpointRow), "checkpoints"),
        (typeof(RepositoryRevisionRow), "revisions"), (typeof(RepositoryReceiptRow), "receipts"),
        (typeof(RepositoryPathRow), "paths"), (typeof(RepositoryDeliveryRow), "deliveries"),
        (typeof(RepositoryReferenceRow), "references"), (typeof(RepositoryDeltaRow), "deltas"),
        (typeof(RepositoryMappingRow), "mappings"), (typeof(RepositoryMappingIntervalRow), "mapping_intervals"),
        (typeof(RepositoryMappingRootRow), "mapping_roots"), (typeof(RepositoryMappingClaimRow), "mapping_claims"),
    ];

    internal static void Configure(ModelBuilder model, bool postgres)
    {
        foreach (var (type, table) in Tables)
        {
            var entity = model.Entity(type);
            entity.ToTable((postgres ? "cellbridge_repository_" : "repository_") + table);
            foreach (var property in type.GetProperties())
            {
                var column = entity.Property(property.PropertyType, property.Name);
                if (property.PropertyType == typeof(string))
                {
                    column.IsRequired();
                    if (property.Name == "Json")
                    {
                        column.HasColumnType(postgres ? "text" : "varbinary(max)");
                        if (!postgres) column.HasConversion(new ValueConverter<string, byte[]>(value => DocumentEncoding.Encode(value), value => DocumentEncoding.Decode(value)));
                    }
                    if (property.Name == "PathKey") column.HasColumnType(postgres ? "text" : "nvarchar(max)");
                    if (property.Name is not ("Json" or "PathKey")) column.HasMaxLength(property.Name == "Kind" ? 16 : 64).IsUnicode(false);
                    if (property.Name == "PathKey") column.UseCollation(postgres ? "C" : "Latin1_General_100_BIN2");
                }
                if (property.PropertyType == typeof(decimal) || property.PropertyType == typeof(decimal?)) column.HasPrecision(20, 0);
                if (property.PropertyType == typeof(Guid)) column.ValueGeneratedNever();
            }
        }
        model.Entity<RepositoryHeadRow>().HasKey(x => x.ResourceId);
        model.Entity<RepositoryHeadRow>().HasIndex(x => new { x.IsDeleted, x.PathHash }).HasDatabaseName("IX_repository_heads_0");
        model.Entity<RepositoryHeadRow>().HasIndex(x => new { x.IsDeleted, x.DeliveryBlocked, x.PendingDeliveries, x.ResourceId }).HasDatabaseName("IX_repository_heads_1");
        model.Entity<RepositoryCoordinationRow>().HasKey(x => x.ResourceId);
        model.Entity<RepositoryEditorRow>().HasKey(x => new { x.ResourceId, x.ClientId });
        model.Entity<RepositoryEditorRow>().HasIndex(x => new { x.ResourceId, x.ExpiresUtc }).HasDatabaseName("IX_repository_editors_0");
        model.Entity<RepositoryPartitionRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition });
        model.Entity<RepositoryElementRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.ElementKey });
        model.Entity<RepositoryElementRow>().HasIndex(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.SerialGuid, x.SerialValue }).HasDatabaseName("IX_repository_elements_0");
        model.Entity<RepositoryVisibilityRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.ElementKey, x.StartSequence });
        model.Entity<RepositoryVisibilityRow>().HasIndex(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.EndSequence, x.StartSequence }).HasDatabaseName("IX_repository_visibility_0");
        model.Entity<RepositoryGraphRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.Kind, x.OwnerKey, x.RecordKey });
        model.Entity<RepositoryDeltaRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.Sequence });
        model.Entity<RepositoryVisibilityRow>().HasIndex(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.ElementKey, x.EndSequence }).HasDatabaseName("IX_repository_visibility_1");
        model.Entity<RepositoryMappingRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.MappingKey });
        model.Entity<RepositoryMappingIntervalRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.MappingKey, x.StartSequence });
        model.Entity<RepositoryMappingIntervalRow>().HasIndex(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.EndSequence, x.StartSequence }).HasDatabaseName("IX_repository_mapping_intervals_0");
        model.Entity<RepositoryMappingRootRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.IndexKey });
        model.Entity<RepositoryMappingRootRow>().HasIndex(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.Sequence }).HasDatabaseName("IX_repository_mapping_roots_0");
        model.Entity<RepositoryMappingClaimRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.SerialGuid, x.SerialValue });
        model.Entity<RepositoryBindingRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.BindingKey });
        model.Entity<RepositoryBindingChangeRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.Sequence, x.BindingKey });
        model.Entity<RepositoryCheckpointRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Partition, x.Sequence, x.BindingKey });
        model.Entity<RepositoryRevisionRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.RevisionNumber });
        model.Entity<RepositoryReceiptRow>().HasKey(x => new { x.ResourceId, x.Lifecycle, x.Family, x.Partition, x.OperationHash });
        model.Entity<RepositoryPathRow>().HasKey(x => x.PathHash);
        model.Entity<RepositoryPathRow>().HasIndex(x => x.ResourceId).HasDatabaseName("IX_repository_paths_0");
        model.Entity<RepositoryDeliveryRow>().HasKey(x => new { x.ResourceId, x.OperationId });
        model.Entity<RepositoryDeliveryRow>().HasIndex(x => new { x.ResourceId, x.Sequence }).HasDatabaseName("IX_repository_deliveries_0").IsUnique();
        model.Entity<RepositoryReferenceRow>().HasKey(x => new { x.ResourceId, x.ObjectKey });
        model.Entity<RepositoryReferenceRow>().Property(x => x.IsPending).HasDefaultValue(false);
        model.Entity<RepositoryReferenceRow>().Property(x => x.AdmissionBytes).HasDefaultValue(0L);
        foreach (var (type, table) in Tables)
        {
            var key = model.Model.FindEntityType(type)!.FindPrimaryKey()!;
            model.Entity(type).HasKey(key.Properties.Select(property => property.Name).ToArray()).HasName("PK_repository_" + table);
        }
    }
}
