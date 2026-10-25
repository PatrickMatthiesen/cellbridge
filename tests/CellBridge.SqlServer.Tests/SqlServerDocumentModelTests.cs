using System.Text;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CellBridge.SqlServer.Tests;

public sealed class SqlServerDocumentModelTests
{
    [Fact]
    public void SharedRowsKeepTheExistingSchemaAndUtf8BinaryRepresentation()
    {
        using var db = new SqlServerDocumentDbContext(new DbContextOptionsBuilder<SqlServerDocumentDbContext>()
            .UseSqlServer("Server=localhost;Database=model_inspection;Integrated Security=true",
                sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable,
                    SqlServerDocumentDbContext.Schema)).Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var model = db.GetService<IDesignTimeModel>().Model;
        foreach (var table in new[] { "snapshots", "metadata", "revisions", "recovery_receipts" })
        {
            var entity = model.GetEntityTypes().Single(row => row.GetTableName() == table);
            var column = entity.GetProperties().Single(property => property.GetColumnName(StoreObjectIdentifier.Table(table, SqlServerDocumentDbContext.Schema)) == "Data");
            Assert.Equal("varbinary(max)", column.GetColumnType());
            var converter = column.GetValueConverter()!;
            const string json = "{\"path\":\"資料-é-😀.docx\"}";
            var bytes = Assert.IsType<byte[]>(converter.ConvertToProvider(json));
            Assert.Equal(Encoding.UTF8.GetBytes(json), bytes);
            Assert.Equal(json, converter.ConvertFromProvider(bytes));
            Assert.Throws<StorageCorruptionException>(() => converter.ConvertFromProvider(new byte[] { 255 }));
        }
    }
}
