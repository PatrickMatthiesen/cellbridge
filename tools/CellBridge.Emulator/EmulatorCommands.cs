using System.Globalization;
using System.Text.Json;
using CellBridge.Client;
using CellBridge.Client.Excel;

static class EmulatorCommands
{
    internal static async Task WatchAsync(ICellBridgeClientTransport transport, Uri document, bool editor,
        CancellationToken ct)
    {
        await using var session = await CellBridgeDocumentSession.OpenAsync(transport, document,
            new() { Mode = editor ? CellBridgeDocumentSessionMode.EagerWriter : CellBridgeDocumentSessionMode.Reader }, ct);
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var notifications = PrintNotificationsAsync(session, listening.Token);
        try
        {
            var previous = session.GetSnapshot().Revision;
            Console.WriteLine($"QueryChanges revision {previous}");
            PrintEditors(session);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var revision = (await session.SyncAsync(ct)).Revision;
                Console.WriteLine($"QueryChanges revision {revision} ({(previous == revision ? "unchanged" : "changed")})");
                previous = revision;
                PrintEditors(session);
            }
        }
        finally
        {
            listening.Cancel();
            await notifications;
        }
    }

    internal static async Task<int> SaveAsync(ICellBridgeClientTransport transport, Uri document, uint sheet,
        string address, ExcelCellValue value, CancellationToken ct)
    {
        await using var session = await CellBridgeDocumentSession.OpenAsync(transport, document,
            new() { Mode = CellBridgeDocumentSessionMode.EagerWriter }, ct);
        var workbook = new ExcelWorkbookSession(session);
        var result = await workbook.SetCellsAsync(sheet, [new(address, value)], session.GetSnapshot().Revision,
            cancellationToken: ct);
        PrintWrite(result);
        return result.Status == ExcelWriteStatus.Success ? 0 : 1;
    }

    internal static async Task InteractiveAsync(ICellBridgeClientTransport transport, Uri document, CancellationToken ct)
    {
        await using var session = await CellBridgeDocumentSession.OpenAsync(transport, document,
            new() { Mode = CellBridgeDocumentSessionMode.LazyWriter }, ct);
        var workbook = new ExcelWorkbookSession(session);
        Console.WriteLine("editors | sheets | read <sheet> <range> | set <sheet> <cell> <json> | clear <sheet> <range>");
        Console.WriteLine("add-sheet <name> | sync | retry | quit");

        var revision = session.GetSnapshot().Revision;
        while (true)
        {
            if (!Console.IsInputRedirected)
                Console.Write("> ");
            var line = await Console.In.ReadLineAsync(ct);
            if (line is null)
                return;
            var fields = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
                continue;
            var arguments = fields.Length == 2 ? fields[1].Trim() : "";
            try
            {
                switch (fields[0])
                {
                    case "quit":
                        return;
                    case "sync":
                        revision = await workbook.SyncAsync(ct);
                        Console.WriteLine(revision);
                        break;
                    case "retry":
                        var retried = await workbook.RetryPendingCommitAsync(ct);
                        PrintWrite(retried);
                        if (retried.Status == ExcelWriteStatus.Success)
                            revision = retried.Revision;
                        break;
                    case "editors":
                        PrintEditors(session);
                        break;
                    case "sheets":
                        foreach (var sheet in workbook.ListSheets())
                            Console.WriteLine($"{sheet.SheetId} {sheet.Name}");
                        break;
                    case "read":
                        var read = ParseSheetArguments(arguments, 2);
                        var range = workbook.ReadRangeAtRevision(read.Sheet, read.Fields[0], 1000);
                        revision = range.Revision;
                        Console.WriteLine(JsonSerializer.Serialize(range));
                        break;
                    case "set":
                        var set = ParseSheetArguments(arguments, 3);
                        var saved = await workbook.SetCellsAsync(set.Sheet, [new(set.Fields[0], ParseValue(set.Fields[1]))],
                            revision, cancellationToken: ct);
                        PrintWrite(saved);
                        if (saved.Status == ExcelWriteStatus.Success)
                            revision = saved.Revision;
                        break;
                    case "clear":
                        var clear = ParseSheetArguments(arguments, 2);
                        var cleared = await workbook.ClearRangeAsync(clear.Sheet, clear.Fields[0], revision,
                            cancellationToken: ct);
                        PrintWrite(cleared);
                        if (cleared.Status == ExcelWriteStatus.Success)
                            revision = cleared.Revision;
                        break;
                    case "add-sheet":
                        var added = await workbook.AddSheetAsync(arguments, cancellationToken: ct);
                        PrintWrite(added);
                        if (added.Status == ExcelWriteStatus.Success)
                            revision = added.Revision;
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown command '{fields[0]}'.");
                        break;
                }
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or
                InvalidOperationException or UnauthorizedAccessException or IOException or HttpRequestException)
            {
                Console.Error.WriteLine(error.Message);
            }
        }
    }

    internal static ExcelCellValue ParseValue(string value)
    {
        using var json = JsonDocument.Parse(value);
        var element = json.RootElement;
        return element.ValueKind switch
        {
            JsonValueKind.String when element.GetString()!.StartsWith('=') => ExcelCellValue.Formula(element.GetString()!),
            JsonValueKind.String => ExcelCellValue.String(element.GetString()!),
            JsonValueKind.Number => new(ExcelCellType.Number, element.GetRawText()),
            JsonValueKind.True or JsonValueKind.False => ExcelCellValue.Boolean(element.GetBoolean()),
            JsonValueKind.Null => ExcelCellValue.Blank,
            _ => throw new ArgumentException("Enter a JSON string, number, boolean, or null."),
        };
    }

    private static (uint Sheet, string[] Fields) ParseSheetArguments(string arguments, int count)
    {
        var fields = arguments.Split(' ', count, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != count || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sheet) ||
            sheet == 0)
            throw new ArgumentException("Supply a numeric sheet ID followed by the address or range and any value.");
        return (sheet, fields[1..]);
    }

    internal static void PrintEditors(CellBridgeDocumentSession session) =>
        Console.WriteLine($"EditorsTable {JsonSerializer.Serialize(session.GetEditors())}");

    private static void PrintWrite(ExcelWriteResult result)
    {
        Console.WriteLine($"{result.Status} {result.Revision}");
        if (result.Detail is not null)
            Console.WriteLine(result.Detail);
        if (result.AddedSheetId is { } sheetId)
            Console.WriteLine($"Added sheet {sheetId}");
        foreach (var conflict in result.Conflicts)
            Console.WriteLine(JsonSerializer.Serialize(conflict));
    }

    private static async Task PrintNotificationsAsync(CellBridgeDocumentSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var notification in session.ReadNotificationsAsync(ct))
                Console.WriteLine($"RTC {notification}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException error) { Console.Error.WriteLine($"RTC disconnected. {error.Message}"); }
    }
}
