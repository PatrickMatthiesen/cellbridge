using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

internal sealed partial class EfListEngine
{
    private static long StoredRecordBytes(string json) => System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.Nodes.JsonNode.Parse(json)!.ToJsonString());
    private static bool RequiresPublicationReadback => true;
    private static async Task ReloadPublicationAsync(ListDbContext db, CancellationToken ct, params object[] entities)
    {
        foreach (var entity in entities)
        {
            if (db.Entry(entity).State == EntityState.Detached) throw InvalidStored();
            await db.Entry(entity).ReloadAsync(ct);
            if (db.Entry(entity).State == EntityState.Detached) throw InvalidStored();
        }
    }
}
