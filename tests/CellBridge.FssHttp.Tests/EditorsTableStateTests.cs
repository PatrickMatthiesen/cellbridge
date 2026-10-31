using System.IO.Compression;
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
        Assert.Equal(clientId.ToString("D"), (string?)editor.Element("CacheID"));
        Assert.Equal("Jane Doe", (string?)editor.Element("FriendlyName"));
        Assert.Equal("Jane Doe", (string?)editor.Element("LoginName"));
        Assert.Equal("true", (string?)editor.Element("HasEditorPermission"));
        Assert.True(long.TryParse((string?)editor.Element("Timeout"), out var timeoutTicks));
        Assert.True(timeoutTicks > DateTime.UtcNow.Ticks);

        var elementNames = editor.Elements().Select(element => element.Name.LocalName).ToArray();
        Assert.Equal(
            new[] { "CacheID", "FriendlyName", "LoginName", "HasEditorPermission", "Timeout" },
            elementNames);
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
        Assert.Equal(new[] { "CacheID", "FriendlyName", "LoginName", "SIPAddress", "EmailAddress", "HasEditorPermission", "Timeout" },
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
        var groups = response.DataElementPackage!.DataElements
            .Where(element => element.DataElementType == DataElementType.ObjectGroupDataElementData)
            .Select(ObjectGroupDataElement.ParseOpaque).ToArray();
        var objects = Assert.Single(groups, group => group.Objects.Any(item =>
            item.Content.SequenceEqual(EditorsTablePartitionBuilder.ZipStreamHeader))).Objects;
        Assert.True(objects.Count >= 2);
        Assert.Equal(EditorsTablePartitionBuilder.ZipStreamHeader, objects[0].Content);
        using var compressed = new MemoryStream(objects[1].Content, writable: false);
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        using var xml = new MemoryStream();
        inflater.CopyTo(xml);
        var editor = Assert.Single(XDocument.Parse(System.Text.Encoding.UTF8.GetString(xml.ToArray()))
            .Root!.Elements("Editor"));

        Assert.Equal(clientId.ToString("D"), (string?)editor.Element("CacheID"));
        Assert.Equal("sip:editor@example.test", (string?)editor.Element("SIPAddress"));
        Assert.Equal("editor@example.test", (string?)editor.Element("EmailAddress"));
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
