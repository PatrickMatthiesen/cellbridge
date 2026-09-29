using System.IO.Compression;
using System.Xml.Linq;
using OfficeCollabServer.Storage;
using Xunit;

namespace OfficeCollabServer.FssHttp.Tests;

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
