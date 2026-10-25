using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

#if CELLBRIDGE_SQLSERVER
namespace CellBridge.Lists.SqlServer;
#else
namespace CellBridge.Lists.PostgreSql;
#endif

internal sealed partial class EfListEngine
{
    private static IQueryable<RecordRow> ApplyFilter(IQueryable<RecordRow> records, ListField field, RecordFilter filter)
    {
        var row = Expression.Parameter(typeof(RecordRow), "row");
        Expression predicate;
        if (filter.Value is null)
        {
            predicate = FieldIsNull(field, row);
            if (filter.Operator == FilterOperator.NotEqual) predicate = Expression.Not(predicate);
        }
        else
        {
            var comparison = FilterComparison(field, filter, row);
            var zero = Expression.Constant(0, comparison.Type);
            predicate = filter.Operator switch
            {
                FilterOperator.Equal => Expression.Equal(comparison, zero),
                FilterOperator.NotEqual => Expression.NotEqual(comparison, zero),
                FilterOperator.GreaterThan => Expression.GreaterThan(comparison, zero),
                FilterOperator.GreaterOrEqual => Expression.GreaterThanOrEqual(comparison, zero),
                FilterOperator.LessThan => Expression.LessThan(comparison, zero),
                FilterOperator.LessOrEqual => Expression.LessThanOrEqual(comparison, zero),
                _ => throw Validation("Unknown query operator.")
            };
        }
        return records.Where(Expression.Lambda<Func<RecordRow, bool>>(predicate, row));
    }

    private static IOrderedQueryable<RecordRow> ApplyOrder(IQueryable<RecordRow> records, ListField? field, bool descending)
    {
        var row = Expression.Parameter(typeof(RecordRow), "row");
        var id = Expression.Lambda(RecordIdKey(row), row);
        if (field is null) return OrderExpression(records, id, descending, false);
        var value = SortKey(field, row);
        var isNull = Expression.Lambda(Expression.Equal(value, Expression.Constant(null, value.Type)), row);
        var ordered = OrderExpression(records, isNull, !descending, false);
        ordered = OrderExpression(ordered, Expression.Lambda(value, row), descending, true);
        return OrderExpression(ordered, id, descending, true);
    }

    private static IOrderedQueryable<RecordRow> OrderExpression(IQueryable<RecordRow> records,
        LambdaExpression key, bool descending, bool then)
    {
        var method = then ? descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy)
            : descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy);
        return (IOrderedQueryable<RecordRow>)records.Provider.CreateQuery<RecordRow>(Expression.Call(
            typeof(Queryable), method, [typeof(RecordRow), key.ReturnType], records.Expression, Expression.Quote(key)));
    }

    private static IQueryable<RecordRow?> PageAfterRecordId(IQueryable<RecordRow> records,
        IQueryable<RecordRow> anchors, bool descending, int limit) => PageAfterSort(records, anchors, null, descending, limit);

    private static IQueryable<RecordRow?> PageAfterSort(IQueryable<RecordRow> records,
        IQueryable<RecordRow> anchors, ListField? field, bool descending, int limit)
    {
        var row = Expression.Parameter(typeof(RecordRow), "candidate");
        var anchor = Expression.Parameter(typeof(RecordRow), "anchor");
        var idComparison = RecordIdComparison(row, anchor);
        var idAfter = CompareToZero(idComparison, descending);
        Expression predicate = idAfter;
        if (field is not null)
        {
            var rowNull = FieldIsNull(field, row);
            var anchorNull = FieldIsNull(field, anchor);
            var comparison = SortComparison(field, row, anchor);
            var equal = Expression.OrElse(Expression.AndAlso(rowNull, anchorNull),
                Expression.AndAlso(Expression.AndAlso(Expression.Not(rowNull), Expression.Not(anchorNull)),
                    Expression.Equal(comparison, Expression.Constant(0, comparison.Type))));
            var nullAfter = descending
                ? Expression.AndAlso(rowNull, Expression.Not(anchorNull))
                : Expression.AndAlso(Expression.Not(rowNull), anchorNull);
            predicate = Expression.OrElse(nullAfter, Expression.OrElse(CompareToZero(comparison, descending),
                Expression.AndAlso(equal, idAfter)));
        }
        var candidates = records.Where(Expression.Lambda<Func<RecordRow, bool>>(predicate, row));
        var page = ApplyOrder(candidates, field, descending).Take(limit).DefaultIfEmpty();
        // The anchor parameter remains in the correlated subquery until SelectMany
        // binds it. A missing anchor produces no rows; an exhausted page has one null.
        var collection = Expression.Lambda<Func<RecordRow, IEnumerable<RecordRow>>>(page.Expression, anchor);
        return anchors.SelectMany(collection, (_, candidate) => candidate);
    }

    private static Expression CompareToZero(Expression comparison, bool descending) => descending
        ? Expression.LessThan(comparison, Expression.Constant(0, comparison.Type))
        : Expression.GreaterThan(comparison, Expression.Constant(0, comparison.Type));

    private static IOrderedQueryable<RecordRow?> ApplyPageOrder(IQueryable<RecordRow?> records, ListField? field, bool descending) =>
        ApplyOrder(records!, field, descending)!;

    private static Expression Substitute<T>(Expression<T> expression, params Expression[] arguments)
    {
        Expression body = expression.Body;
        for (var i = 0; i < arguments.Length; i++) body = new ReplaceParameter(expression.Parameters[i], arguments[i]).Visit(body);
        return body;
    }

    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : base.VisitParameter(node);
    }
}
