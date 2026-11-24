using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CellBridge.Client;
using CellBridge.Client.Excel;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};

try
{
    return await RunAsync(args, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    return 130;
}
catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or
    UnauthorizedAccessException or InvalidDataException or IOException or HttpRequestException or JsonException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static async Task<int> RunAsync(string[] args, CancellationToken ct)
{
    if (args.Length == 0 || args is ["--help" or "-h"])
    {
        PrintUsage();
        return args.Length == 0 ? 2 : 0;
    }

    if (args.Length < 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var document) ||
        document.Scheme is not ("http" or "https") || document.UserInfo.Length != 0 ||
        document.Query.Length != 0 || document.Fragment.Length != 0)
        throw new ArgumentException("Supply the absolute HTTP(S) document URL as the second argument.");

    var origin = new Uri(document.GetLeftPart(UriPartial.Authority));
    var mount = Environment.GetEnvironmentVariable("CELLBRIDGE_PATH_BASE") ?? "";
    switch (args)
    {
        case ["word" or "powerpoint", _, ..]:
            return await OfficeCommands.RunAsync(origin, mount, document, args[0], args[2..], ct);
        case ["scenario", _, "coauthor", var workers, var scenarioSheet] when
            int.TryParse(workers, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 2 and <= 32:
            await Scenarios.CoauthorAsync(origin, mount, document, count, ParseSheet(scenarioSheet), ct);
            return 0;
        case ["scenario", _, "conflict", _, _] or ["scenario", _, "conflict", _, _, "--local-wins" or "--remote-wins"]:
            var policy = args.Length == 5 ? ExcelConflictPolicy.FailWithConflict :
                args[5] == "--local-wins" ? ExcelConflictPolicy.LocalWins : ExcelConflictPolicy.RemoteWins;
            await Scenarios.ConflictAsync(origin, mount, document, ParseSheet(args[3]), args[4], policy, ct);
            return 0;
        case ["watch", _] or ["watch", _, "--editor"]:
            using (var connection = await EmulatorConnection.ConnectAsync(origin, mount, ct))
                await EmulatorCommands.WatchAsync(connection.Transport, document, args.Length == 3, ct);
            return 0;
        case ["interactive", _]:
            using (var connection = await EmulatorConnection.ConnectAsync(origin, mount, ct))
                await EmulatorCommands.InteractiveAsync(connection.Transport, document, ct);
            return 0;
        case ["save", _, var sheet, var address, var value]:
            using (var connection = await EmulatorConnection.ConnectAsync(origin, mount, ct))
                return await EmulatorCommands.SaveAsync(connection.Transport, document, ParseSheet(sheet), address,
                    EmulatorCommands.ParseValue(value), ct);
        default:
            PrintUsage();
            return 2;
    }
}

static uint ParseSheet(string value) =>
    uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint sheet) && sheet > 0
        ? sheet : throw new ArgumentException("Supply a numeric sheet ID. The interactive sheets command lists IDs.");

static void PrintUsage() => Console.WriteLine("""
    CellBridge.Emulator watch <document-url> [--editor]
    CellBridge.Emulator interactive <document-url>
    CellBridge.Emulator save <document-url> <sheet-id> <cell> <json-value>
    CellBridge.Emulator scenario <document-url> coauthor <accounts> <sheet-id>
    CellBridge.Emulator scenario <document-url> conflict <sheet-id> <cell> [--local-wins|--remote-wins]
    CellBridge.Emulator word <document-url> [--editor] [--account <number>] [--script <file|->]
    CellBridge.Emulator powerpoint <document-url> [--editor] [--account <number>] [--script <file|->]

    Set CELLBRIDGE_BEARER_TOKEN or CELLBRIDGE_LOGIN and CELLBRIDGE_PASSWORD.
    Set CELLBRIDGE_PASSWORD_STDIN=1 to read one password line instead.
    Scenarios and --account use numbered credentials such as CELLBRIDGE_LOGIN_1 and CELLBRIDGE_PASSWORD_1.
    Set CELLBRIDGE_PATH_BASE if CellBridge is mounted beneath the origin.
    Cell values use JSON strings, numbers, booleans, or null. A string beginning with '=' is a formula.
    Run scenarios on a disposable workbook. They save changes.
    Word and PowerPoint accept one command per line. Use --script - for fail-fast stdin.
    Use --editor to join Coauth on open. --account selects numbered credentials.
    Run help within a Word or PowerPoint session for commands.
    """);

sealed class EmulatorConnection : IDisposable
{
    private readonly HttpClient _http;
    public HttpCellBridgeTransport Transport { get; }

    private EmulatorConnection(HttpClient http, Uri origin, string mount)
    {
        _http = http;
        Transport = new HttpCellBridgeTransport(http, origin, mount);
    }

    public static async Task<EmulatorConnection> ConnectAsync(Uri origin, string mount, CancellationToken ct,
        int? account = null)
    {
        var suffix = account is null ? "" : "_" + account.Value.ToString(CultureInfo.InvariantCulture);
        var bearer = Environment.GetEnvironmentVariable("CELLBRIDGE_BEARER_TOKEN" + suffix);
        var login = Environment.GetEnvironmentVariable("CELLBRIDGE_LOGIN" + suffix);
        var password = Environment.GetEnvironmentVariable("CELLBRIDGE_PASSWORD" + suffix);
        var http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
        });
        try
        {
            if (!string.IsNullOrEmpty(bearer))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            else if (!string.IsNullOrEmpty(login))
            {
                if (password is null && account is null &&
                    Environment.GetEnvironmentVariable("CELLBRIDGE_PASSWORD_STDIN") == "1")
                    password = await Console.In.ReadLineAsync(ct);
                if (password is null)
                    throw new ArgumentException($"Set CELLBRIDGE_PASSWORD{suffix} to sign in to WebDemo.");
                await WebDemoLogin.SignInAsync(http, origin, login, password, ct, applicationPathBase: mount);
            }
            else if (account is not null)
                throw new ArgumentException($"Configure credentials for scenario account {account}.");

            return new EmulatorConnection(http, origin, mount);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public void Dispose() => _http.Dispose();
}
