using System.IO.Compression;
using System.Buffers.Binary;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;
using Xunit;

namespace CellBridge.FssHttp.Tests;

public sealed class EditorsTableStateTests
{
    [Fact]
    public void JoinSession_ProducesSchemaOrderedEditorEntryWithExpirationTicks()
    {
        var store = new DocumentStore();
        var document = store.GetOrAdd("/shared/test.docx");
        var clientId = Guid.NewGuid();

        document.JoinEditingSession(clientId, 3600, asEditor: true, userName: "Jane Doe");

        var editor = Assert.Single(ReadEditorsTable(document).Elements("Editor"));
        Assert.Equal(clientId.ToString("B"), (string?)editor.Element("CacheID"));
        Assert.Equal("Jane Doe", (string?)editor.Element("FriendlyName"));
        Assert.Equal("Jane Doe", (string?)editor.Element("LoginName"));
        Assert.Equal("true", (string?)editor.Element("HasEditorPermission"));
        Assert.True(long.TryParse((string?)editor.Element("Timeout"), out var timeoutTicks));
        Assert.True(timeoutTicks > DateTime.UtcNow.Ticks);

        var elementNames = editor.Elements().Select(element => element.Name.LocalName).ToArray();
        Assert.Equal(
            new[] { "CacheID", "FriendlyName", "LoginName", "HasEditorPermission", "Timeout", "Metadata" },
            elementNames);
        var joined = Assert.Single(editor.Element("Metadata")!.Elements());
        Assert.Equal("JoinCoauthTime", joined.Name.LocalName);
        Assert.Equal(DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(Convert.FromBase64String(joined.Value))),
            document.Sessions.Single().JoinedUtc);
    }

    [Fact]
    public void JoinSession_PersistsOptionalOwnerEmailAndSipAddressInXmlTable()
    {
        var store = new DocumentStore();
        var document = store.GetOrAdd("/shared/test.docx");
        var clientId = Guid.NewGuid();
        var owner = new CellBridge.Storage.Abstractions.SubjectIdentity("subject", "login", "Editor")
        {
            SipAddress = "sip:editor@example.test",
            EmailAddress = "editor@example.test",
        };

        document.JoinEditingSession(clientId, 3600, asEditor: true, userName: owner.Login, owner: owner);

        var editor = Assert.Single(ReadEditorsTable(document).Elements("Editor"));
        Assert.Equal("Editor", (string?)editor.Element("FriendlyName"));
        Assert.Equal("login", (string?)editor.Element("LoginName"));
        Assert.Equal("sip:editor@example.test", (string?)editor.Element("SIPAddress"));
        Assert.Equal("editor@example.test", (string?)editor.Element("EmailAddress"));
        Assert.Contains(editor.Element("Metadata")!.Elements(), element => element.Name.LocalName == "JoinCoauthTime");
        Assert.Equal(new[] { "CacheID", "FriendlyName", "LoginName", "SIPAddress", "EmailAddress", "HasEditorPermission", "Timeout", "Metadata" },
            editor.Elements().Select(element => element.Name.LocalName));
    }

    [Fact]
    public async Task BinaryEditorQueryIncludesPersistedOwnerEmailAndSipAddress()
    {
        var provider = TestStorage.Memory();
        var owner = TestActor.Value with
        {
            Identity = TestActor.Value.Identity with
            {
                EmailAddress = "editor@example.test",
                SipAddress = "sip:editor@example.test",
            },
        };
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/binary-editor.docx", MinimalDocx.Create(), owner))!;
        var head = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        var clientId = Guid.NewGuid();
        var joined = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId,
            TestStorage.Admit(head, owner), new FssHttpSubRequest
            {
                Type = SubRequestType.EditorsTable,
                SubRequestDataAttributes =
                {
                    ["EditorsTableRequestType"] = "JoinEditingSession",
                    ["ClientID"] = clientId.ToString("D"),
                    ["Timeout"] = "3600",
                    ["AsEditor"] = "true",
                },
            }));
        Assert.True(joined.Changed);

        var response = (await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { new(RequestTypes.QueryChanges)
            {
                RequestId = 1,
                TargetPartitionId = StoredDocument.EditorsTablePartitionId,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true },
            } } }, new Dictionary<string, string>(), owner)).Response;
        var query = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(response.SubResponses).Data);
        var stream = PartitionGraphSnapshot.Create(response.DataElementPackage!.DataElements,
            query.StorageIndexExtendedGuid).Materialize();
        var headerLength = EditorsTablePartitionBuilder.ZipStreamHeader.Length;
        Assert.Equal(EditorsTablePartitionBuilder.ZipStreamHeader, stream[..headerLength]);
        using var compressed = new MemoryStream(stream, headerLength, stream.Length - headerLength, writable: false);
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        using var xml = new MemoryStream();
        inflater.CopyTo(xml);
        var editor = Assert.Single(XDocument.Parse(System.Text.Encoding.UTF8.GetString(xml.ToArray()))
            .Root!.Elements("Editor"));

        Assert.Equal(clientId.ToString("B"), (string?)editor.Element("CacheID"));
        Assert.Equal("sip:editor@example.test", (string?)editor.Element("SIPAddress"));
        Assert.Equal("editor@example.test", (string?)editor.Element("EmailAddress"));
        var joinMetadata = editor.Element("Metadata")!.Elements().Single(element => element.Name.LocalName == "JoinCoauthTime");
        var control = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.Equal(Assert.Single(control.Editors).JoinedUtc, DateTime.FromFileTimeUtc(
            BinaryPrimitives.ReadInt64LittleEndian(Convert.FromBase64String(joinMetadata.Value))));
    }

    [Fact]
    public void JoinCoauthTime_RemainsStableAcrossRefreshAndDoesNotConsumeClientMetadataQuota()
    {
        var document = new DocumentStore().GetOrAdd("/shared/test.xlsx");
        var joinedUtc = new DateTime(2026, 10, 30, 20, 40, 22, DateTimeKind.Utc);
        document.UseAuthoritativeTime(joinedUtc);
        var session = document.JoinEditingSession(Guid.NewGuid(), 3600, asEditor: true, userName: "Editor");

        var initial = Assert.Single(ReadEditorsTable(document).Elements("Editor"));
        var metadata = initial.Element("Metadata")!;
        var value = Assert.Single(metadata.Elements());
        Assert.Equal("JoinCoauthTime", value.Name.LocalName);
        Assert.Equal(joinedUtc.ToFileTimeUtc(), BinaryPrimitives.ReadInt64LittleEndian(Convert.FromBase64String(value.Value)));
        Assert.Empty(session.Metadata);

        foreach (var key in new[] { "JoinCoauthTime", "Capabilities", "Platform", "ClientValue" })
            Assert.True(document.UpdateEditorMetadata(session.ClientId, key, [1]));
        Assert.Equal(4, Assert.Single(document.Sessions).Metadata.Count);

        document.UseAuthoritativeTime(joinedUtc.AddMinutes(10));
        document.JoinEditingSession(session.ClientId, 3600, asEditor: true, userName: "Editor");
        var refreshed = Assert.Single(ReadEditorsTable(document).Elements("Editor"));
        var refreshedValue = Assert.Single(refreshed.Element("Metadata")!.Elements(), element => element.Name.LocalName == "JoinCoauthTime");
        Assert.Equal(value.Value, refreshedValue.Value);
        Assert.Equal(4, refreshed.Element("Metadata")!.Elements().Count());
        Assert.Equal(4, Assert.Single(document.Sessions).Metadata.Count);
    }

    [Fact]
    public void Metadata_UpdateAndRemove_RebuildsEditorsTable()
    {
        var store = new DocumentStore();
        var document = store.GetOrAdd("/shared/test.docx");
        var clientId = Guid.NewGuid();
        document.JoinEditingSession(clientId, 3600, asEditor: true);

        Assert.True(document.UpdateEditorMetadata(clientId, "Platform", "Win32"u8.ToArray()));
        var metadata = Assert.Single(
            Assert.Single(ReadEditorsTable(document).Elements("Editor")).Elements("Metadata"));
        Assert.Equal("V2luMzI=", (string?)metadata.Element("Platform"));

        Assert.True(document.RemoveEditorMetadata(clientId, "Platform"));
        Assert.DoesNotContain(
            Assert.Single(ReadEditorsTable(document).Elements("Editor")).Elements("Metadata"),
            element => element.Name.LocalName == "Platform");
    }

    [Fact]
    public void LeaveSession_RemovesEditorEntry()
    {
        var store = new DocumentStore();
        var document = store.GetOrAdd("/shared/test.docx");
        var clientId = Guid.NewGuid();
        document.JoinEditingSession(clientId, 3600, asEditor: true);

        Assert.True(document.LeaveSession(clientId));
        Assert.Empty(ReadEditorsTable(document).Elements("Editor"));
    }

    private static XElement ReadEditorsTable(StoredDocument document)
    {
        var bytes = document.EditorsTablePartition.Content;
        Assert.True(bytes.Length > 8);
        Assert.Equal(new byte[] { 0x1A, 0x5A, 0x3A, 0x30, 0, 0, 0, 0 }, bytes[..8]);

        using var compressed = new MemoryStream(bytes, 8, bytes.Length - 8, writable: false);
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        using var xml = new MemoryStream();
        inflater.CopyTo(xml);
        return XDocument.Parse(System.Text.Encoding.UTF8.GetString(xml.ToArray())).Root!;
    }
}
