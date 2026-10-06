using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Npgsql;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class PostgreSqlTests
{
    [PostgreSqlFact]
    public async Task CommitAndDestinationAcknowledgementLossRecoverThroughBothDurableReceipts()
    {
        await using var database = await IsolatedPostgreSqlDatabase.CreateAsync();
        using var files = new TemporaryDirectory();
        var baseline = TestOfficeFile.Docx("baseline");
        var changed = TestOfficeFile.Docx("committed before lost response");
        Guid resourceId;
        CellBridge.FssHttpB.FsshttpbCellRequest request;
        Guid operationId;
        string committedHash;
        string destinationRevision;

        await using (var firstSource = database.CreateSource())
        await using (var first = new DocumentLibraryHarness(files.Path,
            new(new PostgreSqlStateStore(firstSource), new PostgreSqlContentStore(firstSource)),
            new ThrowAfterManifestReplacementFaults()))
        {
            var state = await first.UploadAsync(baseline);
            resourceId = state.ResourceId;
            request = DocumentLibraryHarness.SaveRequest(state, changed);
            // Treat the returned execution as a response that was lost before the caller observed it.
            _ = await first.Documents.ExecuteAsync(resourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), DocumentLibraryHarness.Owner);
            var committed = (await first.Provider.State.FindByResourceIdAsync(resourceId))!;
            committedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(changed));
            Assert.Equal(committedHash, committed.Content.Sha256);
            Assert.Single(committed.Receipts);
            Assert.Single(committed.Publication!.Pending);
            operationId = committed.Publication.Pending[0].OperationId;
            Assert.Equal(baseline, await first.Destination.ReadCurrentBytesAsync(resourceId));

            Assert.Equal(CellBridge.AspNetCore.PublicationAttempt.TransientFailure,
                await first.Publisher.PublishNextAsync(resourceId));
            var afterLostDestinationReply = (await first.Provider.State.FindByResourceIdAsync(resourceId))!;
            Assert.Equal(committedHash, afterLostDestinationReply.Content.Sha256);
            Assert.Single(afterLostDestinationReply.Receipts);
            Assert.Single(afterLostDestinationReply.Publication!.Pending);
            var manifest = await first.Destination.GetAsync(resourceId);
            Assert.True(manifest.Receipts.ContainsKey(operationId));
            destinationRevision = manifest.CurrentRevision;
            Assert.Equal(changed, await first.Destination.ReadCurrentBytesAsync(resourceId));
        }

        await using var restartedSource = database.CreateSource();
        await using var restarted = new DocumentLibraryHarness(files.Path,
            new(new PostgreSqlStateStore(restartedSource), new PostgreSqlContentStore(restartedSource)));
        await restarted.Library.InitializeAsync();
        var replay = await restarted.Documents.ExecuteAsync(resourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), DocumentLibraryHarness.Owner);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(committedHash, replay.State.Content.Sha256);
        Assert.Single(replay.State.Receipts);
        Assert.Single(replay.State.Publication!.Pending);
        Assert.Equal(1, await restarted.Worker.PublishPendingOnceAsync());
        Assert.Equal(changed, await restarted.Destination.ReadCurrentBytesAsync(resourceId));
        var manifestAfterReplay = await restarted.Destination.GetAsync(resourceId);
        Assert.Equal(destinationRevision, manifestAfterReplay.CurrentRevision);
        Assert.Single(manifestAfterReplay.Receipts);
        Assert.True(manifestAfterReplay.Receipts.ContainsKey(operationId));
        Assert.Empty(((await restarted.Provider.State.FindByResourceIdAsync(resourceId))!).Publication!.Pending);
    }

    private sealed class ThrowAfterManifestReplacementFaults : CellBridge.DocumentLibrary.IDestinationFaults
    {
        public ValueTask AfterManifestReplacementAsync(CellBridge.DocumentLibrary.LibraryManifest manifest,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("Destination acknowledgement was lost after manifest replacement."));
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")))
            Skip = "Set ConnectionStrings__cellbridge to an administrative disposable PostgreSQL database.";
    }
}

internal sealed class IsolatedPostgreSqlDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private readonly string _connectionString;

    private IsolatedPostgreSqlDatabase(string adminConnectionString, string databaseName, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        _connectionString = connectionString;
    }

    public static async Task<IsolatedPostgreSqlDatabase> CreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")
            ?? throw new InvalidOperationException("ConnectionStrings__cellbridge is required.");
        var admin = new NpgsqlConnectionStringBuilder(configured);
        if (string.IsNullOrWhiteSpace(admin.Database)) admin.Database = "postgres";
        var databaseName = "cellbridge_document_library_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
        var isolated = new NpgsqlConnectionStringBuilder(configured) { Database = databaseName };
        var result = new IsolatedPostgreSqlDatabase(admin.ConnectionString, databaseName, isolated.ConnectionString);
        await using var source = result.CreateSource();
        await new PostgreSqlStateStore(source).InitializeAsync();
        return result;
    }

    public NpgsqlDataSource CreateSource() => NpgsqlDataSource.Create(_connectionString);

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
