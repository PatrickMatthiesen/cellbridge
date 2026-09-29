using OfficeCollabServer.Storage;

namespace OfficeCollabServer.FssHttp.Tests;

public sealed class DocumentStoreConcurrencyTests
{
    [Fact]
    public void List_ReturnsAnIndependentSnapshot()
    {
        var store = new DocumentStore();
        var first = store.Put("/shared/first.docx", [1]);
        var second = store.Put("/shared/second.xlsx", [2]);

        var snapshot = store.List();
        store.Put("/shared/third.pptx", [3]);

        Assert.Equal(2, snapshot.Length);
        Assert.Equal(new[] { first, second }, snapshot);
        Assert.Equal(3, store.List().Length);
    }

    [Fact]
    public void Documents_IsolateContentAndSessions()
    {
        var store = new DocumentStore();
        var word = store.Put("/shared/report.docx", [1, 2, 3]);
        var workbook = store.Put("/shared/budget.xlsx", [4, 5, 6]);
        var wordClients = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var workbookClients = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();

        Parallel.Invoke(
            () => Parallel.ForEach(wordClients, client => word.JoinSession(client)),
            () => Parallel.ForEach(workbookClients, client => workbook.JoinSession(client)));

        Assert.Equal(wordClients.Length, word.Sessions.Count);
        Assert.Equal(workbookClients.Length, workbook.Sessions.Count);
        Assert.All(word.Sessions, session => Assert.Contains(session.ClientId, wordClients));
        Assert.All(workbook.Sessions, session => Assert.Contains(session.ClientId, workbookClients));
        Assert.DoesNotContain(word.Sessions, session => workbookClients.Contains(session.ClientId));
        Assert.DoesNotContain(workbook.Sessions, session => wordClients.Contains(session.ClientId));
        Assert.Equal(new byte[] { 1, 2, 3 }, word.Content);
        Assert.Equal(new byte[] { 4, 5, 6 }, workbook.Content);
    }

    [Fact]
    public void ConcurrentContentUpdates_AreSerializedPerDocument()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/concurrent.docx", [0]);
        const int writes = 64;

        Parallel.For(0, writes, value => document.SetContent([(byte)value]));

        Assert.Equal((uint)writes + 1, document.ContentVersion);
        Assert.Single(document.Content);
        Assert.InRange(document.Content[0], (byte)0, (byte)(writes - 1));
    }

    [Fact]
    public void ReturnedContentAndSessionViews_DoNotExposeMutableStoreState()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/snapshot.docx", [1, 2, 3]);
        var client = Guid.NewGuid();
        var joined = document.JoinSession(client);

        var content = document.Content;
        content[0] = 99;
        joined.Metadata["ClientValue"] = [9];

        Assert.Equal(new byte[] { 1, 2, 3 }, document.Content);
        Assert.Empty(Assert.Single(document.Sessions).Metadata);
    }
}
