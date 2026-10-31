using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Tests;

public static class ControlReadBatchScenario
{
    public static async Task RunAsync(StorageProvider provider, Func<IInterceptor[], IDocumentRepository> observed,
        bool combinedControlSnapshot = false)
    {
        var actor = new CellBridgeActor(new("control:owner", "owner", "Owner"), CanCreate: true);
        var seed = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/control-batch.docx", MinimalDocx.Create(),
            DocumentSecurity.Create(actor.Identity), provider.Content, provider.Limits);
        var now = DateTime.UtcNow;
        seed = seed with { Coordination = seed.Coordination with { Generation = 9 }, Editors = Enumerable.Range(1, 3)
            .Select(number => new EditorState(Guid.NewGuid(), "editor" + number, now, now, now.AddHours(1), 3600, true, number,
                ImmutableDictionary<string, ImmutableArray<byte>>.Empty, actor.Identity)).ToImmutableArray() };
        Assert.True((await provider.Documents.CreateAsync(seed)).Created);
        var initial = (await provider.Documents.ReadControlAsync(seed.Head.ResourceId))!;
        var trace = new ControlCommands(async () =>
        {
            var changed = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(initial.Head.ResourceId,
                Admit(initial.Head), initial.Head.Security with { Grants = initial.Head.Security.Grants.Add("other", DocumentAccess.Read) }, initial.Head.AuthorityGeneration));
            Assert.True(changed.Changed);
        });
        var repository = observed([trace]);
        var beforeReadUtc = DateTime.UtcNow.AddSeconds(-1);
        var selected = (await repository.ReadControlAsync(initial.Head.ResourceId))!;
        Assert.True(trace.Injected);
        Assert.Equal(initial.Head.StateVersion, selected.Head.StateVersion);
        Assert.Equal(initial.Head.AuthorityGeneration, selected.Head.AuthorityGeneration);
        Assert.Equal(initial.Coordination.Generation, selected.Coordination.Generation);
        Assert.False(selected.Head.Security.Grants.ContainsKey("other"));
        Assert.Equal(DateTimeKind.Utc, selected.UtcNow.Kind);
        Assert.InRange(selected.UtcNow, beforeReadUtc, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(3, selected.Editors.Length);
        Assert.Equal(3, selected.Editors.Select(editor => editor.ClientId).Distinct().Count());
        Assert.Equal(initial.Editors.Select(editor => editor.ClientId), selected.Editors.Select(editor => editor.ClientId));
        Assert.Equal(JsonSerializer.Serialize(initial.Partitions), JsonSerializer.Serialize(selected.Partitions));
        string readTag = combinedControlSnapshot ? "CellBridge coherent control snapshot" : "CellBridge coherent control batch";
        var batch = Assert.Single(trace.Sql, sql => sql.Contains(readTag, StringComparison.Ordinal));
        Assert.Equal(1, trace.ControlRows);
        Assert.Contains("UNION ALL", batch, StringComparison.Ordinal);
        Assert.Equal(combinedControlSnapshot ? 1 : 2, trace.Sql.Count(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.Contains(readTag, StringComparison.Ordinal)));
        var current = (await provider.Documents.ReadControlAsync(initial.Head.ResourceId))!;
        Assert.True(current.Head.Security.Grants.ContainsKey("other"));
        Assert.True(current.Coordination.Generation > selected.Coordination.Generation);

        trace.Clear();
        var preparation = (await repository.PrepareSaveAsync(initial.Head.ResourceId, new(0,
            new(initial.Head.LifecycleGeneration, SaveOperationFamily.Protocol, 0, "control-batch"), [])))!;
        Assert.Equal(current.Head.StateVersion, preparation.Control.Head.StateVersion);
        var reused = Assert.Single(trace.Sql, sql => sql.Contains("CellBridge coherent control batch", StringComparison.Ordinal));
        Assert.DoesNotContain("repository_partitions", reused, StringComparison.Ordinal);
        Assert.Equal(1, trace.Sql.Count(sql => sql.Contains("repository_partitions", StringComparison.Ordinal)));
        Assert.Equal(JsonSerializer.Serialize(initial.Partitions), JsonSerializer.Serialize(preparation.Control.Partitions));

        trace.Clear();
        var command = new DocumentControlCommand.SetPermissions(current.Head.ResourceId, Admit(current.Head),
            current.Head.Security with { Grants = current.Head.Security.Grants.Add("third", DocumentAccess.Read) }, current.Head.AuthorityGeneration);
        Assert.True((await repository.ExecuteControlAsync(command)).Changed);
        var guardedBatch = Assert.Single(trace.Sql, sql => sql.Contains("CellBridge coherent control batch", StringComparison.Ordinal));
        Assert.DoesNotContain("clock_timestamp", guardedBatch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SYSUTCDATETIME", guardedBatch, StringComparison.OrdinalIgnoreCase);
        Assert.Single(trace.Sql, sql => sql.Contains("clock_timestamp", StringComparison.OrdinalIgnoreCase) || sql.Contains("SYSUTCDATETIME", StringComparison.OrdinalIgnoreCase));

        DocumentActorAdmission Admit(DocumentHead head) => new(actor.Identity, head.ResourceId, head.LifecycleGeneration,
            head.Security.AuthorizationPolicy, DocumentAccess.Write) { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };
    }

    private sealed class ControlCommands(Func<Task> inject) : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public bool Injected;
        public int ControlRows;
        public void Clear() { Sql.Clear(); ControlRows = 0; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Injected && command.CommandText.Contains("repository_heads", StringComparison.Ordinal))
            { Injected = true; try { await inject(); } catch { await result.DisposeAsync(); throw; } }
            if (command.CommandText.Contains("CellBridge coherent control snapshot", StringComparison.Ordinal) ||
                command.CommandText.Contains("CellBridge coherent control batch", StringComparison.Ordinal)) ControlRows++;
            return result;
        }
    }
}
