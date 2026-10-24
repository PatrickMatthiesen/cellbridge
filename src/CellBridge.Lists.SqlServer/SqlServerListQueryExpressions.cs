using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

internal sealed partial class EfListEngine
{
    private static Expression ScalarKey(ListField field, Expression row)
    {
        var key = field.Id.ToString("D");
        var type = (int)field.Type;
        return Substitute<Func<RecordRow, string?>>(value => SqlServerListDbContext.ScalarKey(value.ValuesJson, key, type), row);
    }
    private static Expression FieldIsNull(ListField field, Expression row) => Expression.Equal(ScalarKey(field, row), Expression.Constant(null, typeof(string)));
    private static Expression SortKey(ListField field, Expression row) =>
        Substitute<Func<string?,string?>>(value => EF.Functions.Collate(value!, "Latin1_General_100_BIN2"), ScalarKey(field, row));
    private static Expression RecordIdKey(Expression row) => Substitute<Func<RecordRow,string>>(value =>
        EF.Functions.Collate(SqlServerListDbContext.UuidKey(value.RecordId), "Latin1_General_100_BIN2"), row);
    private static Expression RecordIdComparison(Expression row, Expression anchor) =>
        Substitute<Func<string?,string?,int?>>((a,b) => SqlServerListDbContext.KeyCompare(a,b), RecordIdKey(row), RecordIdKey(anchor));
    private static Expression SortComparison(ListField field, Expression row, Expression anchor) =>
        Substitute<Func<string?,string?,int?>>((a,b) => SqlServerListDbContext.KeyCompare(a,b), ScalarKey(field,row), ScalarKey(field,anchor));
    private static Expression FilterComparison(ListField field, RecordFilter filter, Expression row)
    {
        var key = field.Id.ToString("D");
        var type = (int)field.Type;
        var value = new JsonObject { [key] = filter.Value!.DeepClone() }.ToJsonString();
        return Substitute<Func<string?,int?>>(scalar => SqlServerListDbContext.KeyCompare(scalar,
            SqlServerListDbContext.ScalarKey(value, key, type)), ScalarKey(field,row));
    }
}
