#nullable enable
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace CellBridge.Interop.Tests;

// Scenario adaptations from OfficeDev/Interop-TestSuites S02_Coauth.cs.
// See NOTICE-Interop-TestSuites.md for the pinned source, scope and MIT notice.
[Collection("WebDemo integration")]
public sealed class StatefulCoauthoringTests
{
    [LiveInteropFact]
    public async Task RepeatedJoinAndExitPreserveOtherClientsAndReleaseTheLastLease()
    {
        // S02 TC06, TC14, TC15, TC27, TC28 and TC29. Retrying a completed
        // operation models an acknowledgement the client did not retain.
        using var session = await Session.CreateAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var schema = Guid.NewGuid();
        var joined = await session.Coauth(a, schema, "JoinCoauthoring");
        Success(joined, "Alone");
        Success(await session.Coauth(a, schema, "GetCoauthoringStatus"), "Alone");
        var transition = (string?)joined.Data.Attribute("TransitionID");
        Assert.False(string.IsNullOrWhiteSpace(transition));
        var retry = await session.Coauth(a, schema, "JoinCoauthoring");
        Success(retry, "Alone");
        Assert.Equal(transition, (string?)retry.Data.Attribute("TransitionID"));
        Success(await session.Coauth(b, schema, "JoinCoauthoring"), "Coauthoring");
        Success(await session.Coauth(a, schema, "GetCoauthoringStatus"), "Coauthoring");
        Success(await session.Coauth(b, schema, "GetCoauthoringStatus"), "Coauthoring");
        Success(await session.Coauth(a, schema, "RefreshCoauthoring"), "Coauthoring");
        Success(await session.Coauth(a, schema, "ExitCoauthoring"));
        Success(await session.Coauth(a, schema, "ExitCoauthoring"));
        // Observe before refresh: RefreshCoauthoring can recreate a deleted lease.
        Success(await session.Coauth(b, schema, "GetCoauthoringStatus"), "Alone");
        Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(a, Guid.NewGuid(), "JoinCoauthoring")).Code);
        Success(await session.Coauth(b, schema, "RefreshCoauthoring"), "Alone");
        Success(await session.Coauth(b, schema, "ExitCoauthoring"));
        Success(await session.Coauth(b, schema, "ExitCoauthoring"));
        // A different schema can acquire only after the last participant exits.
        var nextSchema = Guid.NewGuid();
        Success(await session.Coauth(a, nextSchema, "JoinCoauthoring"), "Alone");
        Success(await session.Coauth(a, nextSchema, "ExitCoauthoring"));
    }

    [LiveInteropFact]
    public async Task DifferentSchemaCannotJoinRefreshOrReleaseTheActiveSession()
    {
        // S02 TC01, TC04, TC18 and TC23/TC31, followed by a successful
        // acquisition to distinguish a rejected request from a leaked lease.
        // The two-participant Exit differs from TC18/SE Success. The later
        // one-participant delayed Exit is TC17-like. See the notice.
        using var session = await Session.CreateAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var schema = Guid.NewGuid();
        var otherSchema = Guid.NewGuid();
        Success(await session.Coauth(a, schema, "JoinCoauthoring"), "Alone");
        Success(await session.Coauth(b, schema, "JoinCoauthoring"), "Coauthoring");
        foreach (var operation in new[] { "JoinCoauthoring", "RefreshCoauthoring", "ExitCoauthoring", "CheckLockAvailability" })
            Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(a, otherSchema, operation)).Code);
        Success(await session.Coauth(a, schema, "GetCoauthoringStatus"), "Coauthoring");
        Success(await session.Coauth(b, schema, "GetCoauthoringStatus"), "Coauthoring");
        Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(Guid.NewGuid(), otherSchema, "JoinCoauthoring")).Code);
        Success(await session.Coauth(a, schema, "RefreshCoauthoring"), "Coauthoring");
        Success(await session.Coauth(a, schema, "ExitCoauthoring"));
        Success(await session.Coauth(b, schema, "GetCoauthoringStatus"), "Alone");
        Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(a, otherSchema, "JoinCoauthoring")).Code);
        Success(await session.Coauth(b, schema, "RefreshCoauthoring"), "Alone");
        Success(await session.Coauth(b, schema, "ExitCoauthoring"));
        Success(await session.Coauth(a, otherSchema, "JoinCoauthoring"), "Alone");
        // A delayed exit from the old session must not release the new schema.
        Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(a, schema, "ExitCoauthoring")).Code);
        Success(await session.Coauth(a, otherSchema, "GetCoauthoringStatus"), "Alone");
        Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(b, schema, "JoinCoauthoring")).Code);
        Success(await session.Coauth(a, otherSchema, "RefreshCoauthoring"), "Alone");
        Success(await session.Coauth(a, otherSchema, "ExitCoauthoring"));
    }

    [LiveInteropFact]
    public async Task ConflictingExitWithOneParticipantPreservesItsSchemaLease()
    {
        // TC17-like single-account regression, not the distinct-account upstream
        // scenario. SE/2019 document Success; CellBridge currently rejects it.
        using var session = await Session.CreateAsync();
        var owner = Guid.NewGuid();
        var schema = Guid.NewGuid();
        var otherSchema = Guid.NewGuid();
        Success(await session.Coauth(owner, schema, "JoinCoauthoring"), "Alone");
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(owner, otherSchema, "ExitCoauthoring")).Code);
        Success(await session.Coauth(owner, schema, "GetCoauthoringStatus"), "Alone");
        Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(Guid.NewGuid(), otherSchema, "JoinCoauthoring")).Code);
        Success(await session.Coauth(owner, schema, "RefreshCoauthoring"), "Alone");
        Success(await session.Coauth(owner, schema, "ExitCoauthoring"));
        Success(await session.Coauth(owner, otherSchema, "JoinCoauthoring"), "Alone");
        Success(await session.Coauth(owner, otherSchema, "ExitCoauthoring"));
    }

    [LiveInteropFact]
    public async Task ExclusiveLeaseRejectsCoauthorOperationsUntilItsOwnerReleasesIt()
    {
        // S02 TC03, TC16, TC22 and TC30. TC30's upstream implementation
        // sends Join; here Refresh is sent as its title and requirement specify.
        // TC16 and documented SE behavior return Success for this Exit.
        // This regression retains CellBridge's current lock-error behavior;
        // it does not establish Office compatibility. See the notice.
        using var session = await Session.CreateAsync();
        var client = Guid.NewGuid();
        var schema = Guid.NewGuid();
        var exclusive = Guid.NewGuid();
        Success(await session.Exclusive(exclusive, "GetLock"));
        foreach (var operation in new[] { "JoinCoauthoring", "RefreshCoauthoring", "ExitCoauthoring", "CheckLockAvailability" })
            Assert.Equal("FileAlreadyLockedOnServer", (await session.Coauth(client, schema, operation)).Code);
        // Rejected coauthor operations must leave the exclusive owner intact.
        Success(await session.Exclusive(exclusive, "RefreshLock"));
        Assert.Equal("FileAlreadyLockedOnServer", (await session.Exclusive(Guid.NewGuid(), "GetLock")).Code);
        Success(await session.Exclusive(exclusive, "ReleaseLock"));
        Success(await session.Coauth(client, schema, "JoinCoauthoring"), "Alone");
        Success(await session.Coauth(client, schema, "ExitCoauthoring"));
    }

    private static void Success(Reply reply, string? status = null)
    {
        Assert.Equal("Success", reply.Code);
        if (status is not null)
        {
            Assert.Equal(status, (string?)reply.Data.Attribute("CoauthStatus"));
            Assert.Equal("SchemaLock", (string?)reply.Data.Attribute("LockType"));
        }
    }

    private sealed record Reply(string Code, XElement Data);

    private sealed class Session(HttpClient http, Uri endpoint, Uri file) : IDisposable
    {
        private static readonly XNamespace Ns = CellStorageClient.ProtocolNamespace;
        private int token;
        private readonly string? traceDirectory = CreateTraceDirectory();

        private static string? CreateTraceDirectory()
        {
            var root = Environment.GetEnvironmentVariable("CELLBRIDGE_STATEFUL_TRACE_DIRECTORY");
            if (string.IsNullOrWhiteSpace(root)) return null;
            var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public static async Task<Session> CreateAsync()
        {
            var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
            var http = LiveInteropHttp.Create(endpoint);
            try
            {
                var name = "stateful-coauthor-" + Guid.NewGuid().ToString("N");
                using var created = await http.PostAsJsonAsync("/api/documents", new { name, type = "docx" });
                created.EnsureSuccessStatusCode();
                return new Session(http, endpoint, new Uri(LiveInteropHttp.PublicOrigin(endpoint), "/shared/" + name + ".docx"));
            }
            catch { http.Dispose(); throw; }
        }

        public Task<Reply> Coauth(Guid client, Guid schema, string operation) => Send("Coauth",
            new XAttribute("CoauthRequestType", operation), new XAttribute("ClientID", client),
            new XAttribute("SchemaLockID", schema));

        public Task<Reply> Exclusive(Guid exclusive, string operation) => Send("ExclusiveLock",
            new XAttribute("ExclusiveLockRequestType", operation), new XAttribute("ExclusiveLockID", exclusive));

        private async Task<Reply> Send(string type, params XAttribute[] attributes)
        {
            var current = (++token).ToString(System.Globalization.CultureInfo.InvariantCulture);
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            var message = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(Ns + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(Ns + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()),
                    new XElement(Ns + "Request", new XAttribute("Url", file), new XAttribute("RequestToken", current),
                        new XElement(Ns + "SubRequest", new XAttribute("Type", type), new XAttribute("SubRequestToken", current),
                            new XElement(Ns + "SubRequestData", attributes, new XAttribute("Timeout", 3600)))))));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            { Content = new StringContent(message.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
            request.Headers.TryAddWithoutValidation("SOAPAction", CellStorageClient.SoapAction);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var responseText = await response.Content.ReadAsStringAsync();
            if (traceDirectory is not null)
            {
                // Opt-in synthetic fixture evidence. Do not record authentication headers.
                await WriteTrace(current + ".request.xml", message.ToString(SaveOptions.DisableFormatting));
                await WriteTrace(current + ".response.xml", responseText);
            }
            var xml = XDocument.Parse(responseText);
            var fileResponse = Assert.Single(xml.Descendants(Ns + "Response"));
            Assert.Equal(current, (string?)fileResponse.Attribute("RequestToken"));
            var sub = Assert.Single(fileResponse.Elements(Ns + "SubResponse"));
            Assert.Equal(current, (string?)sub.Attribute("SubRequestToken"));
            return new Reply((string)sub.Attribute("ErrorCode")!, sub.Element(Ns + "SubResponseData") ?? new XElement(Ns + "SubResponseData"));
        }

        private async Task WriteTrace(string name, string contents)
        {
            var path = Path.Combine(traceDirectory!, name);
            await File.WriteAllTextAsync(path, contents);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => http.Dispose();
    }
}
