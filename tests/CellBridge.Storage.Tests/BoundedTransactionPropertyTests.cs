using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;
using Xunit.Abstractions;

namespace CellBridge.Storage.Tests;

public sealed class BoundedTransactionPropertyTests(ITestOutputHelper output)
{
    private const int Seed = 0x18_34;

    [Fact]
    public Task BoundedCompleteSaveFailuresAndReceiptsPreservePersistedState() =>
        Check(new(new InMemoryStateStore(), new InMemoryContentStore()));

    [PostgreSqlFact]
    public async Task PostgreSqlBoundedSaveFailuresAndReceiptsSurviveProviderRecreation()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        StorageProvider Create() => new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
        await Check(Create(), Create);
    }

    private async Task Check(StorageProvider provider, Func<StorageProvider>? recreate = null)
    {
        var random = new Random(Seed);
        for (int sample = 0; sample < 8; sample++)
        {
            output.WriteLine($"seed={Seed} sample={sample} mutation=partial/stale/retry");
            var state = (await new CellBridgeDocumentService(provider).CreateAsync(
                "/bounded-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
            var request = StorageTests.Fixture("save-first");
            var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
            put.ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!);
            put.Flags |= sample % 2 == 0 ? (byte)0x20 : (byte)0;
            put.AdditionalFlagsBits = sample % 3 == 0 ? (ushort)0x8004 : (ushort)0x04;
            request.SubRequests[0].RequestId = (ulong)random.Next(1, 1000);
            var service = new CellBridgeDocumentService(provider);
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
            Assert.Single(saved.State.Receipts);
            string committed = JsonSerializer.Serialize(saved.State);
            byte[] expectedBytes = (await StoredDocument.RestoreAsync(saved.State, provider.Content)).FilePartition.FileGraph.Materialize();

            provider = recreate?.Invoke() ?? provider;
            service = new(provider);
            var retry = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.Equal(committed, JsonSerializer.Serialize(retry.State));
            Assert.Equal(saved.Response.ToByteArray(), retry.Response.ToByteArray());

            foreach (byte partial in new byte[] { 0x02, 0x04 })
            {
                var invalid = new FsshttpbCellRequest { DataElementPackage = request.DataElementPackage,
                    SubRequests = { new(RequestTypes.PutChanges) { RequestId = 2000 + (ulong)partial,
                        Data = new PutChangesSubRequestData { Flags = partial,
                            StorageIndex = partial == 0x02 ? ExGuid.Null : put.StorageIndex,
                            ExpectedStorageIndex = put.ExpectedStorageIndex } } } };
                var rejected = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                    invalid, new Dictionary<string, string>(), TestActor.Value);
                Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, Assert.Single(rejected.Response.SubResponses).Error!.ErrorCode);
                Assert.Equal(committed, JsonSerializer.Serialize(rejected.State));
            }
            var stale = StorageTests.Fixture("save-second");
            Assert.IsType<PutChangesSubRequestData>(stale.SubRequests[0].Data).ExpectedStorageIndex =
                StorageIds.Restore(state.Partitions[0].StorageIndex!);
            var failure = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                stale, new Dictionary<string, string>(), TestActor.Value);
            var staleResponse = Assert.Single(failure.Response.SubResponses);
            Assert.True(staleResponse.Status);
            Assert.Equal(ErrorType.Cell, staleResponse.Error!.Type);
            Assert.Equal((ulong)CellErrorCode.CoherencyFailure, staleResponse.Error.ErrorCode);
            Assert.Equal(committed, JsonSerializer.Serialize(failure.State));

            provider = recreate?.Invoke() ?? provider;
            var persisted = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Equal(committed, JsonSerializer.Serialize(persisted));
            Assert.Equal(expectedBytes, (await StoredDocument.RestoreAsync(persisted, provider.Content)).FilePartition.FileGraph.Materialize());
        }
    }
}
