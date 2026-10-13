using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class RecordReviewRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task UnrelatedPatchPreservesReferenceAfterTargetReadIsRevoked()
    {
        var workspace = await database.NewWorkspaceAsync("reference-patch-revocation");
        var label = new ListField(Guid.NewGuid(), "Label", ListFieldType.Text);
        var target = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Targets", [label]));
        var firstTargetId = Guid.NewGuid();
        var secondTargetId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id,
            new CreateRecordRequest(firstTargetId, 1, Values(label, "first")));
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, target.Id,
            new CreateRecordRequest(secondTargetId, 1, Values(label, "second")));

        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: label.Id);
        var note = new ListField(Guid.NewGuid(), "Note", ListFieldType.Text);
        var source = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Sources", [reference, note]));
        var sourceId = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, source.Id,
            new CreateRecordRequest(sourceId, 1, new JsonObject
            {
                [reference.Id.ToString("D")] = firstTargetId.ToString("D"),
                [note.Id.ToString("D")] = "before"
            }));

        var editor = new ListActor("test:reference-editor:" + Guid.NewGuid().ToString("N"));
        await database.Engine.SetMembershipAsync(database.AdminActor, workspace, editor.Subject, true, false, false);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, source.Id, editor.Subject,
            ListAccess.Read | ListAccess.Write);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, target.Id, editor.Subject, ListAccess.Read);
        await database.Engine.SetListGrantAsync(database.AdminActor, workspace, target.Id, editor.Subject, ListAccess.None);

        var updated = await database.Engine.UpdateRecordAsync(editor, workspace, source.Id, sourceId,
            new UpdateRecordRequest(1, 1, [new FieldChange(note.Id, JsonValue.Create("after"))]));
        Assert.Equal("after", updated.Values[note.Id.ToString("D")]!.GetValue<string>());
        Assert.Equal(firstTargetId.ToString("D"), updated.Values[reference.Id.ToString("D")]!.GetValue<string>());

        var assignment = await Assert.ThrowsAsync<ListException>(() => database.Engine.UpdateRecordAsync(editor,
            workspace, source.Id, sourceId, new UpdateRecordRequest(2, 1,
                [new FieldChange(reference.Id, JsonValue.Create(secondTargetId.ToString("D")))])));
        Assert.Equal(ListFailure.NotFound, assignment.Failure);
        var resolution = await Assert.ThrowsAsync<ListException>(() => database.Engine.ResolveReferenceAsync(editor,
            workspace, source.Id, sourceId, reference.Id));
        Assert.Equal(ListFailure.NotFound, resolution.Failure);
    }

    [PostgreSqlFact]
    public async Task ReceiptReplayUsesDatabaseExpiryAndOriginalSchemaValidation()
    {
        var workspace = await database.NewWorkspaceAsync("receipt-review-regressions");
        var integer = new ListField(Guid.NewGuid(), "Count", ListFieldType.Integer);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new CreateListRequest(Guid.NewGuid(), "Receipts", [integer]));

        var original = new CreateRecordRequest(Guid.NewGuid(), 1, NumberValues(integer, "1"));
        var created = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, original);
        var optional = new ListField(Guid.NewGuid(), "Later", ListFieldType.Text);
        await database.Engine.PublishSchemaAsync(database.AdminActor, workspace, list.Id,
            new PublishSchemaRequest(1, list.Name, [integer, optional]));
        var replay = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, original);
        Assert.True(replay.Replayed);
        Assert.Equal((created.Revision, created.Number), (replay.Revision, replay.Number));

        var typed = new CreateRecordRequest(Guid.NewGuid(), 2, NumberValues(integer, "1"));
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, typed);
        var invalidReplay = typed with { Values = NumberValues(integer, "1.0") };
        Assert.Equal(ListValidation.CreateDigest(typed), ListValidation.CreateDigest(invalidReplay));
        var invalid = await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, invalidReplay));
        Assert.Equal(ListFailure.Validation, invalid.Failure);

        var expiring = new CreateRecordRequest(Guid.NewGuid(), 2, NumberValues(integer, "2"));
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, expiring);
        await using (var expire = database.AdminSource!.CreateCommand("""
            UPDATE cellbridge_lists.create_receipts SET expires_utc=clock_timestamp()
            WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3
            """))
        {
            expire.Parameters.AddWithValue(workspace);
            expire.Parameters.AddWithValue(list.Id);
            expire.Parameters.AddWithValue(expiring.Id);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync());
        }
        var expired = await Assert.ThrowsAsync<ListException>(() =>
            database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, expiring));
        Assert.Equal(ListFailure.Conflict, expired.Failure);

        long receiptCount = -1;
        await database.SetWorkspaceContextAsync(workspace, async (connection, transaction) =>
        {
            await using var count = new NpgsqlCommand("""
                SELECT count(*) FROM cellbridge_lists.create_receipts
                WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3
                """, connection, transaction);
            count.Parameters.AddWithValue(workspace);
            count.Parameters.AddWithValue(list.Id);
            count.Parameters.AddWithValue(expiring.Id);
            receiptCount = (long)(await count.ExecuteScalarAsync())!;
        });
        Assert.Equal(0, receiptCount);
    }

    private static JsonObject Values(ListField field, string value) =>
        new() { [field.Id.ToString("D")] = value };

    private static JsonObject NumberValues(ListField field, string number) =>
        new() { [field.Id.ToString("D")] = JsonNode.Parse(number) };
}
