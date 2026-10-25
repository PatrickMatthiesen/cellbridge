using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.PostgreSql;

internal sealed partial class EfListEngine
{
    private static long StoredRecordBytes(string json) => System.Text.Encoding.UTF8.GetByteCount(json);
    private static bool RequiresPublicationReadback => false;
    private static Task ReloadPublicationAsync(ListDbContext db, CancellationToken ct, params object[] entities)
    {
        return Task.CompletedTask;
    }
}
