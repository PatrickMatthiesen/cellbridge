using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

internal sealed partial class PostgreSqlDocumentDatabase
{
    public IQueryable<ObjectRow> ObjectsAtKeys(CellBridgeDbContext db, IReadOnlyList<string> keys) =>
        QueryObjectsAtKeys(db, keys);
    internal static IQueryable<ObjectRow> QueryObjectsAtKeys(CellBridgeDbContext db, IReadOnlyList<string> keys) =>
        db.Set<ObjectRow>().AsNoTracking().Where(row => keys.Contains(row.ObjectKey));
}
