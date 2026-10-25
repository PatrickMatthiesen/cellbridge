using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.PostgreSql;

internal sealed partial class EfListEngine
{
    private static Expression JsonScalar(ListField field, Expression row)
    {
        var key = field.Id.ToString("D");
        return Substitute<Func<RecordRow, string?>>(row => PostgreSqlListDbContext.JsonText(row.ValuesJson, key), row);
    }

    private static Expression FieldIsNull(ListField field, Expression row) => Expression.Equal(JsonScalar(field, row), Expression.Constant(null, typeof(string)));
    private static Expression RecordIdKey(Expression row) => Expression.Property(row, nameof(RecordRow.RecordId));
    private static Expression RecordIdComparison(Expression row, Expression anchor) =>
        Expression.Call(RecordIdKey(row), nameof(Guid.CompareTo), Type.EmptyTypes, RecordIdKey(anchor));

    private static Expression SortKey(ListField field, Expression row)
    {
        var scalar = JsonScalar(field, row);
        return field.Type switch
        {
            ListFieldType.Integer => Substitute<Func<string?, long?>>(value => (long?)Convert.ToInt64(value), scalar),
            ListFieldType.Decimal => Substitute<Func<string?, decimal?>>(value => (decimal?)Convert.ToDecimal(value), scalar),
            ListFieldType.Boolean => Substitute<Func<string?, bool?>>(value => (bool?)Convert.ToBoolean(value), scalar),
            ListFieldType.Text or ListFieldType.Choice => Substitute<Func<string?, byte[]?>>(value => PostgreSqlListDbContext.Utf16Key(value), scalar),
            ListFieldType.Timestamp => Substitute<Func<string?, string?>>(value => EF.Functions.Collate(PostgreSqlListDbContext.TimestampKey(value)!, "C"), scalar),
            _ => Substitute<Func<string?, string?>>(value => EF.Functions.Collate(value!, "C"), scalar)
        };
    }

    private static Expression SortComparison(ListField field, Expression left, Expression right) =>
        CompareScalars(field, JsonScalar(field, left), JsonScalar(field, right));

    private static Expression CompareScalars(ListField field, Expression left, Expression right) => field.Type switch
    {
        ListFieldType.Integer => NumericComparison(Substitute<Func<string?, long?>>(value => (long?)Convert.ToInt64(value), left),
            Substitute<Func<string?, long?>>(value => (long?)Convert.ToInt64(value), right)),
        ListFieldType.Decimal => NumericComparison(Substitute<Func<string?, decimal?>>(value => (decimal?)Convert.ToDecimal(value), left),
            Substitute<Func<string?, decimal?>>(value => (decimal?)Convert.ToDecimal(value), right)),
        ListFieldType.Text or ListFieldType.Choice => Substitute<Func<string?, string?, int>>((a,b) => PostgreSqlListDbContext.Utf16Compare(a,b), left, right),
        ListFieldType.Timestamp => Substitute<Func<string?, string?, int>>((a,b) => PostgreSqlListDbContext.TimestampCompare(a,b), left, right),
        _ => Substitute<Func<string?, string?, int>>((a,b) => PostgreSqlListDbContext.CCompare(a,b), left, right)
    };

    private static Expression NumericComparison(Expression left, Expression right) => Expression.Condition(
        Expression.OrElse(Expression.Equal(left, Expression.Constant(null, left.Type)), Expression.Equal(right, Expression.Constant(null, right.Type))),
        Expression.Constant(null, typeof(int?)), Expression.Condition(Expression.Equal(left,right), Expression.Constant((int?)0, typeof(int?)),
            Expression.Condition(Expression.LessThan(left,right), Expression.Constant((int?)-1, typeof(int?)), Expression.Constant((int?)1, typeof(int?)))));

    private static Expression FilterComparison(ListField field, RecordFilter filter, Expression row)
    {
        var scalar = JsonScalar(field, row);
        using var document = JsonDocument.Parse(filter.Value!.ToJsonString());
        var value = document.RootElement;
        if (field.Type == ListFieldType.Integer)
        {
            var number = value.GetInt64();
            return NumericComparison(Substitute<Func<string?,long?>>(text => (long?)Convert.ToInt64(text), scalar),
                Substitute<Func<long?>>(() => number));
        }
        if (field.Type == ListFieldType.Decimal)
        {
            var number = value.GetDecimal();
            return NumericComparison(Substitute<Func<string?,decimal?>>(text => (decimal?)Convert.ToDecimal(text), scalar),
                Substitute<Func<decimal?>>(() => number));
        }
        if (field.Type == ListFieldType.Boolean)
        {
            var boolean = value.GetBoolean();
            var left = Substitute<Func<string?, bool?>>(text => (bool?)Convert.ToBoolean(text), scalar);
            var right = Substitute<Func<bool?>>(() => boolean);
            // Booleans permit only equality. Null remains distinct from false.
            return Expression.Condition(Expression.Equal(left, Expression.Constant(null, typeof(bool?))), Expression.Constant(null, typeof(int?)),
                Expression.Condition(Expression.Equal(left,right), Expression.Constant((int?)0, typeof(int?)), Expression.Constant((int?)1, typeof(int?))));
        }
        var text = value.GetString();
        return CompareScalars(field, scalar, Substitute<Func<string?>>(() => text));
    }
}
