using System.Globalization;
using System.Text.Json;
using System.Xml;
using CellBridge.Client;
using CellBridge.Client.PowerPoint;
using CellBridge.Client.Word;

static class OfficeCommands
{
    internal static async Task<int> RunAsync(Uri origin, string mount, Uri document, string application,
        string[] options, CancellationToken ct)
    {
        bool editor = false;
        int? account = null;
        string? script = null;
        for (int index = 0; index < options.Length; index++)
        {
            switch (options[index])
            {
                case "--editor" when !editor:
                    editor = true;
                    break;
                case "--account" when account is null && index + 1 < options.Length:
                    account = ParseInt(options[++index], "account number", 1);
                    break;
                case "--script" when script is null && index + 1 < options.Length:
                    script = options[++index];
                    break;
                default:
                    throw new ArgumentException($"Invalid {application} option '{options[index]}'. See --help.");
            }
        }

        using var scriptFile = script is not (null or "-") ? File.OpenText(script) : null;
        using var connection = await EmulatorConnection.ConnectAsync(origin, mount, ct, account);
        await using var documentSession = await CellBridgeDocumentSession.OpenAsync(connection.Transport, document,
            new() { Mode = editor ? CellBridgeDocumentSessionMode.EagerWriter : CellBridgeDocumentSessionMode.LazyWriter }, ct);
        var input = scriptFile ?? Console.In;
        if (application == "word")
        {
            var word = new WordDocumentSession(documentSession);
            var session = new OfficeSession<WordEdit>(() => word.Revision, () => word.HasPendingCommit,
                word.SyncAsync, word.RetryPendingCommitAsync, (before, after, max) => word.ComputeDiff(before, after, max),
                word.ApplyAsync, ParseWordEdit, (command, arguments) => ReadWord(word, command, arguments), WordHelp);
            return await RunLinesAsync(documentSession, session, input, script, ct);
        }

        var presentation = new PowerPointPresentationSession(documentSession);
        var powerpoint = new OfficeSession<PowerPointEdit>(() => presentation.Revision, () => presentation.HasPendingCommit,
            presentation.SyncAsync, presentation.RetryPendingCommitAsync,
            (before, after, max) => presentation.ComputeDiff(before, after, max),
            presentation.ApplyAsync, ParsePowerPointEdit,
            (command, arguments) => ReadPowerPoint(presentation, command, arguments), PowerPointHelp);
        return await RunLinesAsync(documentSession, powerpoint, input, script, ct);
    }

    private static async Task<int> RunLinesAsync<TEdit>(CellBridgeDocumentSession document, OfficeSession<TEdit> session,
        TextReader input, string? script, CancellationToken ct) where TEdit : class
    {
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pinnedBase = null;
        JsonElement? lastOutput = null;
        bool prompt = script is null && !Console.IsInputRedirected;
        if (prompt)
            Console.WriteLine("Enter help for commands.");
        Console.WriteLine($"QueryChanges revision {session.Revision()}");
        EmulatorCommands.PrintEditors(document);

        string ResolveRevision(string value) => value switch
        {
            "current" => session.Revision(),
            ['@', .. var name] => revisions.TryGetValue(name, out var revision)
                ? revision : throw new ArgumentException($"Unknown revision mark '{name}'."),
            _ => value,
        };

        void PrintOutput(object value)
        {
            lastOutput = JsonSerializer.SerializeToElement(value);
            Console.WriteLine(lastOutput.Value.GetRawText());
        }

        for (int lineNumber = 1; ; lineNumber++)
        {
            if (prompt)
                Console.Write("> ");
            var line = await input.ReadLineAsync(ct);
            if (line is null)
                return 0;
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            try
            {
                var (command, arguments) = SplitCommand(line);
                OfficeWriteStatus? expected = null;
                if (command == "expect")
                {
                    var (status, writeCommand) = SplitCommand(arguments);
                    if (!Enum.TryParse<OfficeWriteStatus>(status, ignoreCase: true, out var parsed) ||
                        !string.Equals(status, parsed.ToString(), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"Unknown write status '{status}'.");
                    expected = parsed;
                    (command, arguments) = SplitCommand(writeCommand);
                }

                var edit = session.ParseEdit(command, arguments);
                if (edit is not null || command == "retry")
                {
                    if (command == "retry")
                        RequireNoArguments(command, arguments);
                    var result = edit is not null
                        ? await session.Apply(edit, pinnedBase ?? session.Revision(), ct)
                        : await session.Retry(ct);
                    PrintWrite(result);
                    if ((expected ?? (script is null ? null : OfficeWriteStatus.Success)) is { } required &&
                        result.Status != required)
                        throw new InvalidOperationException($"Expected {required}. Received {result.Status}.");
                    continue;
                }

                if (expected is not null)
                    throw new ArgumentException("expect requires an edit or retry command.");

                switch (command)
                {
                    case "quit":
                        RequireNoArguments(command, arguments);
                        return 0;
                    case "help":
                        RequireNoArguments(command, arguments);
                        Console.WriteLine(SharedHelp);
                        Console.WriteLine(session.Help);
                        break;
                    case "editors":
                        RequireNoArguments(command, arguments);
                        EmulatorCommands.PrintEditors(document);
                        break;
                    case "revision":
                        RequireNoArguments(command, arguments);
                        Console.WriteLine(session.Revision());
                        break;
                    case "sync":
                        RequireNoArguments(command, arguments);
                        Console.WriteLine(await session.Sync(ct));
                        break;
                    case "mark":
                        var name = ReadToken(arguments, "mark <name>");
                        if (name.StartsWith('@'))
                            throw new ArgumentException("Supply a mark name without '@'.");
                        revisions[name] = session.Revision();
                        Console.WriteLine(revisions[name]);
                        break;
                    case "base":
                        var revision = ReadToken(arguments, "base <current|@mark|revision>");
                        pinnedBase = revision == "current" ? null : ResolveRevision(revision);
                        Console.WriteLine(pinnedBase ?? session.Revision());
                        break;
                    case "diff":
                        var diff = SplitFields(arguments);
                        if (diff.Length is < 2 or > 3)
                            throw new ArgumentException("Use diff <before> <after> [max-changes].");
                        PrintOutput(session.Diff(ResolveRevision(diff[0]), ResolveRevision(diff[1]),
                            diff.Length == 3 ? ParseInt(diff[2], "maximum changes", 1) : 1000));
                        break;
                    case "pending":
                        RequireNoArguments(command, arguments);
                        PrintOutput(session.HasPendingCommit());
                        break;
                    case "expect-output":
                        using (var expectedJson = JsonDocument.Parse(arguments))
                        {
                            if (lastOutput is null || !JsonElement.DeepEquals(lastOutput.Value, expectedJson.RootElement))
                                throw new InvalidOperationException($"Expected JSON {arguments}. Received {lastOutput?.GetRawText() ?? "no JSON output"}.");
                        }
                        break;
                    default:
                        var output = session.Read(command, arguments);
                        if (output is null)
                            throw new ArgumentException($"Unknown command '{command}'.");
                        PrintOutput(output);
                        break;
                }
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or XmlException or
                InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or InvalidDataException or
                IOException or HttpRequestException)
            {
                if (script is not null)
                {
                    Console.Error.WriteLine($"{(script == "-" ? "stdin" : script)}:{lineNumber}: {error.Message}");
                    return 1;
                }
                Console.Error.WriteLine(error.Message);
            }
        }
    }

    private static object? ReadWord(WordDocumentSession word, string command, string arguments)
    {
        switch (command)
        {
            case "summary":
                RequireNoArguments(command, arguments);
                return word.GetSummary();
            case "headings":
                RequireNoArguments(command, arguments);
                return word.ListHeadings();
            case "read":
                var fields = SplitFields(arguments);
                if (fields.Length > 2)
                    throw new ArgumentException("Use read [start-index [max-paragraphs]].");
                return word.ReadParagraphs(fields.Length > 0 ? ParseInt(fields[0], "paragraph index", 0) : 0,
                    fields.Length == 2 ? ParseInt(fields[1], "maximum paragraphs", 1) : 1000);
            default:
                return null;
        }
    }

    private static WordEdit? ParseWordEdit(string command, string arguments)
    {
        switch (command)
        {
            case "append":
                return new WordAppendParagraph(ParseText(arguments));
            case "delete":
                return new WordDeleteParagraph(ParseParagraph(ReadToken(arguments, "delete <paragraph>")));
            case "replace" or "insert-before" or "insert-after":
                var (target, text) = SplitCommand(arguments);
                var paragraph = ParseParagraph(target);
                return command == "replace"
                    ? new WordReplaceParagraph(paragraph, ParseText(text))
                    : new WordInsertParagraph(paragraph, ParseText(text), After: command == "insert-after");
            case "comment":
                var (reference, json) = SplitCommand(arguments);
                var fields = JsonSerializer.Deserialize<string?[]>(json);
                if (fields is not ([string, string] or [string, string, _]))
                    throw new ArgumentException("""Use comment <paragraph> ["text","author","optional initials"].""");
                return new WordAddComment(ParseParagraph(reference), fields[0]!, fields[1]!,
                    fields.Length == 3 ? fields[2] : null);
            default:
                return null;
        }
    }

    private static object? ReadPowerPoint(PowerPointPresentationSession presentation, string command, string arguments)
    {
        switch (command)
        {
            case "slides":
                RequireNoArguments(command, arguments);
                return presentation.ListSlides();
            case "read":
                return presentation.ReadSlide(ParseId(ReadToken(arguments, "read <slide-id>"), "slide ID"));
            default:
                return null;
        }
    }

    private static PowerPointEdit? ParsePowerPointEdit(string command, string arguments)
    {
        switch (command)
        {
            case "set-text":
                var (slide, remainder) = SplitCommand(arguments);
                var (shape, text) = SplitCommand(remainder);
                return new PowerPointSetShapeText(ParseId(slide, "slide ID"), ParseId(shape, "shape ID"), ParseText(text));
            case "set-notes":
                var (target, notes) = SplitCommand(arguments);
                return new PowerPointSetSlideNotes(ParseId(target, "slide ID"), ParseText(notes));
            case "add-slide":
                return new PowerPointAddSlide(arguments.Length == 0 ? null : ParseId(arguments, "layout source slide ID"));
            case "delete-slide":
                return new PowerPointDeleteSlide(ParseId(arguments, "slide ID"));
            default:
                return null;
        }
    }

    private static void PrintWrite(OfficeWriteResult result)
    {
        Console.WriteLine($"{result.Status} {result.Revision}");
        if (result.Detail is not null)
            Console.WriteLine(result.Detail);
        if (result.AddedSlideId is { } slideId)
            Console.WriteLine($"Added slide {slideId}");
        foreach (var conflict in result.Conflicts)
            Console.WriteLine(JsonSerializer.Serialize(conflict));
    }

    private static (string Command, string Arguments) SplitCommand(string line)
    {
        var fields = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (fields.Length > 0 ? fields[0] : "", fields.Length == 2 ? fields[1] : "");
    }

    private static string[] SplitFields(string arguments) =>
        arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    private static string ReadToken(string arguments, string usage) =>
        SplitFields(arguments) is [var token] ? token : throw new ArgumentException($"Use {usage}.");

    private static void RequireNoArguments(string command, string arguments)
    {
        if (arguments.Length != 0)
            throw new ArgumentException($"{command} takes no arguments.");
    }

    private static int ParseInt(string value, string name, int minimum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= minimum
            ? number : throw new ArgumentException($"Supply a {name} of at least {minimum}.");

    private static uint ParseId(string value, string name) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && id > 0
            ? id : throw new ArgumentException($"Supply a positive numeric {name}.");

    private static WordParagraphReference ParseParagraph(string value)
    {
        var fields = value.Split(':', 2);
        if (fields is [_, ""])
            throw new ArgumentException("Supply an index or index:paragraph-id.");
        return new(ParseInt(fields[0], "paragraph index", 0), fields.Length == 2 ? fields[1] : null);
    }

    private static string ParseText(string value) =>
        JsonSerializer.Deserialize<string>(value) ?? throw new ArgumentException("Supply a JSON string.");

    private sealed record OfficeSession<TEdit>(
        Func<string> Revision,
        Func<bool> HasPendingCommit,
        Func<CancellationToken, Task<string>> Sync,
        Func<CancellationToken, Task<OfficeWriteResult>> Retry,
        Func<string, string, int, object> Diff,
        Func<TEdit, string, CancellationToken, Task<OfficeWriteResult>> Apply,
        Func<string, string, TEdit?> ParseEdit,
        Func<string, string, object?> Read,
        string Help) where TEdit : class;

    private const string SharedHelp = """
        editors | revision | sync | mark <name> | base <current|@mark|revision>
        diff <before> <after> [max-changes] | pending | retry | quit
        expect <status> <edit-or-retry> | expect-output <json>
        A mark stores the current revision. Use @name or current in diff.
        base pins edits across reads and sync. base current resumes using the latest local revision.
        Scripts require Success for each edit or retry unless expect names another status.
        Write statuses are Success, Conflict, BaseRevisionUnavailable, RetryExhausted, and OutcomeUnknown.
        expect-output checks the most recent JSON read, diff, or pending output.
        After OutcomeUnknown, use pending to check whether retry can replay the unresolved save.
        """;

    private const string WordHelp = """
        summary | headings | read [start-index [max-paragraphs]]
        append <json-text> | replace <paragraph> <json-text> | delete <paragraph>
        insert-before <paragraph> <json-text> | insert-after <paragraph> <json-text>
        comment <paragraph> ["text","author","optional initials"]
        Paragraph references use a zero-based index or index:paragraph-id.
        """;

    private const string PowerPointHelp = """
        slides | read <slide-id>
        set-text <slide-id> <shape-id> <json-text> | set-notes <slide-id> <json-text>
        add-slide [layout-source-slide-id] | delete-slide <slide-id>
        """;
}
