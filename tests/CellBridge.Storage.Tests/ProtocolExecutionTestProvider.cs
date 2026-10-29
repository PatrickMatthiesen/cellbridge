using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using Npgsql;

namespace CellBridge.Storage.Tests;

internal sealed class ProtocolExecutionTestProvider : IAsyncDisposable
{
    private readonly NpgsqlDataSource? source;

    private ProtocolExecutionTestProvider(StorageProvider provider, NpgsqlDataSource? source = null)
    {
        Provider = provider;
        this.source = source;
    }

    public StorageProvider Provider { get; }

    // PostgreSqlFixture owns the environment and schema for the PostgreSQL collection.
    public static ProtocolExecutionTestProvider Create(bool postgreSql)
    {
        if (!postgreSql)
            return new(StorageTestDocuments.Memory());

        var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        return new(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)), source);
    }

    public ValueTask DisposeAsync() => source?.DisposeAsync() ?? ValueTask.CompletedTask;
}
