using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using CellBridge.Client;
using CellBridge.Client.Excel;

static class Scenarios
{
    internal static async Task CoauthorAsync(Uri origin, string mount, Uri document, int count, uint sheet,
        CancellationToken ct)
    {
        await using var participants = new Participants();
        await participants.OpenAsync(origin, mount, document, count, ct);

        var edits = Enumerable.Range(0, count)
            .Select(index => new ExcelCellEdit("A" + (index + 1).ToString(CultureInfo.InvariantCulture),
                ExcelCellValue.String($"CellBridge {Guid.NewGuid():N}")))
            .ToArray();
        var results = await Task.WhenAll(participants.Sessions.Select((session, index) =>
            session.Workbook.SetCellsAsync(sheet, [edits[index]], session.BaseRevision, cancellationToken: ct)));
        for (int index = 0; index < results.Length; index++)
        {
            RequireSuccess(results[index]);
            Console.WriteLine($"Account {index + 1} saved {edits[index].Address} at revision {results[index].Revision}");
        }

        var verifier = participants.Sessions[0];
        await verifier.Document.SyncAsync(ct);
        foreach (var edit in edits)
        {
            if (ReadCell(verifier, sheet, edit.Address) != edit.Value)
                throw new InvalidOperationException($"The merged workbook is missing the edit at {edit.Address}.");
        }
        ValidatePackage(verifier.Document.GetSnapshot().Package);
        Console.WriteLine($"Verified {count} disjoint saves and validated the workbook with OpenXmlValidator.");
    }

    internal static async Task ConflictAsync(Uri origin, string mount, Uri document, uint sheet, string address,
        ExcelConflictPolicy policy, CancellationToken ct)
    {
        await using var participants = new Participants();
        await participants.OpenAsync(origin, mount, document, 2, ct);
        var first = participants.Sessions[0];
        var second = participants.Sessions[1];
        var baseValue = ReadCell(second, sheet, address);
        var firstValue = ExcelCellValue.String($"CellBridge first {Guid.NewGuid():N}");
        var secondValue = ExcelCellValue.String($"CellBridge second {Guid.NewGuid():N}");
        RequireSuccess(await first.Workbook.SetCellsAsync(sheet, [new(address, firstValue)], first.BaseRevision,
            cancellationToken: ct));

        var result = await second.Workbook.SetCellsAsync(sheet, [new(address, secondValue)], second.BaseRevision,
            cancellationToken: ct);
        if (result.Status != ExcelWriteStatus.CellConflict || result.Conflicts is not [var conflict] ||
            conflict.Base != baseValue || conflict.Local != secondValue || conflict.Remote != firstValue)
            throw new InvalidOperationException($"Expected a same-cell conflict at {address}. Received {result.Status}.");

        Console.WriteLine($"Detected a conflict at {address}.");
        if (ReadCell(second, sheet, address) != firstValue)
            throw new InvalidOperationException("The first edit was lost before conflict recovery.");

        var revision = policy == ExcelConflictPolicy.FailWithConflict ? await second.Workbook.SyncAsync(ct) : second.BaseRevision;
        RequireSuccess(await second.Workbook.SetCellsAsync(sheet, [new(address, secondValue)], revision,
            policy: policy, cancellationToken: ct));
        await first.Document.SyncAsync(ct);
        var expected = policy == ExcelConflictPolicy.RemoteWins ? firstValue : secondValue;
        if (ReadCell(first, sheet, address) != expected)
            throw new InvalidOperationException("The recovered save was not visible to the first account.");
        ValidatePackage(first.Document.GetSnapshot().Package);
        Console.WriteLine("Verified same-cell conflict recovery and validated the workbook with OpenXmlValidator.");
    }

    private static ExcelCellValue ReadCell(Participant participant, uint sheet, string address) =>
        participant.Workbook.ReadRange(sheet, address, 1).Single().Value;

    private static void RequireSuccess(ExcelWriteResult result)
    {
        if (result.Status != ExcelWriteStatus.Success)
            throw new InvalidOperationException($"Save returned {result.Status}. {result.Detail}");
    }

    private static void ValidatePackage(byte[] package)
    {
        using var stream = new MemoryStream(package, writable: false);
        using var document = SpreadsheetDocument.Open(stream, false, new OpenSettings
        {
            MarkupCompatibilityProcessSettings = new(MarkupCompatibilityProcessMode.NoProcess, FileFormatVersions.Microsoft365),
        });
        var errors = new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).Take(10).ToArray();
        if (errors.Length != 0)
            throw new InvalidDataException("OpenXmlValidator rejected the workbook: " +
                string.Join("; ", errors.Select(error => error.Description)));
    }

    private sealed record Participant(EmulatorConnection Connection, CellBridgeDocumentSession Document,
        ExcelWorkbookSession Workbook, string BaseRevision);

    private sealed class Participants : IAsyncDisposable
    {
        internal List<Participant> Sessions { get; } = [];

        internal async Task OpenAsync(Uri origin, string mount, Uri document, int count, CancellationToken ct)
        {
            var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index <= count; index++)
            {
                var connection = await EmulatorConnection.ConnectAsync(origin, mount, ct, index);
                CellBridgeDocumentSession? session = null;
                try
                {
                    session = await CellBridgeDocumentSession.OpenAsync(connection.Transport, document,
                        new() { Mode = CellBridgeDocumentSessionMode.EagerWriter, EnableRealTime = false }, ct);
                    if (string.IsNullOrWhiteSpace(session.UserLogin) || !logins.Add(session.UserLogin))
                        throw new ArgumentException($"Scenario account {index} must have a distinct server login.");
                    Sessions.Add(new(connection, session, new ExcelWorkbookSession(session), session.GetSnapshot().Revision));
                }
                catch
                {
                    try
                    {
                        if (session is not null)
                            await session.DisposeAsync();
                    }
                    finally
                    {
                        connection.Dispose();
                    }
                    throw;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception> errors = [];
            foreach (var participant in Sessions)
            {
                try
                {
                    await participant.Document.DisposeAsync();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                finally
                {
                    participant.Connection.Dispose();
                }
            }
            if (errors.Count != 0)
                throw new IOException("A scenario session could not close.", new AggregateException(errors));
        }

    }
}
