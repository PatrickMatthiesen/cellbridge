using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Tests;

public static class NormalizedRelationalSchemaAssertions
{
    public static async Task VerifyAsync(DbContext context, bool sqlServer)
    {
        var tables = context.Model.GetRelationalModel().Tables.Where(table => table.Name.Contains("repository_", StringComparison.Ordinal)).ToArray();
        if (tables.Length != 20) throw new InvalidOperationException("The normalized model must have twenty tables.");
        await context.Database.OpenConnectionAsync();
        var connection = context.Database.GetDbConnection();
        foreach (var table in tables)
        {
            await using var columns = connection.CreateCommand();
            columns.CommandText = sqlServer ? """
                SELECT c.name, t.name + CASE WHEN t.name IN ('nvarchar','varchar','varbinary')
                    THEN '(' + CASE WHEN c.max_length=-1 THEN 'max' ELSE CONVERT(varchar(10),CASE WHEN t.name='nvarchar' THEN c.max_length/2 ELSE c.max_length END) END + ')'
                    WHEN t.name IN ('numeric','decimal') THEN '(' + CONVERT(varchar(10),c.precision) + ',' + CONVERT(varchar(10),c.scale) + ')' ELSE '' END,
                    c.is_nullable FROM sys.columns c JOIN sys.types t ON c.user_type_id=t.user_type_id
                    WHERE c.object_id=OBJECT_ID(@table)
                """ : """
                SELECT a.attname, format_type(a.atttypid,a.atttypmod), NOT a.attnotnull
                FROM pg_attribute a WHERE a.attrelid=to_regclass(@table) AND a.attnum>0 AND NOT a.attisdropped
                """;
            var parameter = columns.CreateParameter(); parameter.ParameterName = "table";
            parameter.Value = (table.Schema ?? (sqlServer ? "dbo" : "public")) + "." + table.Name;
            columns.Parameters.Add(parameter);
            var actual = new Dictionary<string, (string Type, bool Nullable)>(StringComparer.Ordinal);
            await using (var reader = await columns.ExecuteReaderAsync())
                while (await reader.ReadAsync()) actual.Add(reader.GetString(0), (reader.GetString(1), reader.GetBoolean(2)));
            if (actual.Count != table.Columns.Count()) throw new InvalidOperationException(table.Name + " column count differs from its frozen model.");
            foreach (var column in table.Columns)
                if (!actual.TryGetValue(column.Name, out var value) || !string.Equals(value.Type, column.StoreType, StringComparison.OrdinalIgnoreCase) || value.Nullable != column.IsNullable)
                    throw new InvalidOperationException(table.Name + "." + column.Name + " type or nullability differs: actual " + value + ", expected " + column.StoreType + ".");
            await using var indexes = connection.CreateCommand();
            indexes.CommandText = sqlServer ? """
                SELECT i.name, c.name, i.is_unique, ic.key_ordinal FROM sys.indexes i
                JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE i.object_id=OBJECT_ID(@table) AND ic.key_ordinal>0 ORDER BY i.name,ic.key_ordinal
                """ : """
                SELECT idx.relname, a.attname, i.indisunique, key.ordinality::int
                FROM pg_index i JOIN pg_class idx ON idx.oid=i.indexrelid
                CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS key(attnum,ordinality)
                JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=key.attnum
                WHERE i.indrelid=to_regclass(@table) ORDER BY idx.relname,key.ordinality
                """;
            parameter = indexes.CreateParameter(); parameter.ParameterName = "table";
            parameter.Value = (table.Schema ?? (sqlServer ? "dbo" : "public")) + "." + table.Name;
            indexes.Parameters.Add(parameter);
            var actualIndexes = new Dictionary<string, (List<string> Columns, bool Unique)>(StringComparer.Ordinal);
            await using (var reader = await indexes.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(0);
                    if (!actualIndexes.TryGetValue(name, out var value)) actualIndexes[name] = value = ([], reader.GetBoolean(2));
                    value.Columns.Add(reader.GetString(1));
                }
            var expectedIndexes = table.Indexes.Select(index => (index.Name, Names: index.Columns.Select(column => column.Name).ToArray(), index.IsUnique))
                .Append((Name: table.PrimaryKey!.Name, Names: table.PrimaryKey.Columns.Select(column => column.Name).ToArray(), IsUnique: true)).ToArray();
            if (actualIndexes.Count != expectedIndexes.Length) throw new InvalidOperationException(table.Name + " index count differs.");
            foreach (var index in expectedIndexes)
                if (!actualIndexes.TryGetValue(index.Name, out var value) || value.Unique != index.IsUnique || !value.Columns.SequenceEqual(index.Names))
                    throw new InvalidOperationException(table.Name + "." + index.Name + " key columns or uniqueness differ.");
        }
    }
}
