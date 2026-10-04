using System.Text;
using System.Xml.Linq;
using CellBridge.FssHttp;

namespace CellBridge.FssHttp.Tests;

public sealed class MtomStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedRawXmlFailsDuringPreparation(bool nestedResponse)
    {
        var sub = nestedResponse ? new FssHttpSubResponse { SubResponseXml = "<broken>" }
            : new FssHttpSubResponse { SubResponseDataXml = "<broken>" };
        var response = new CellStorageResponse { Responses = { new() { SubResponses = { sub } } } };
        Assert.Throws<System.Xml.XmlException>(() => response.PrepareMtomMessage());
    }

    [Fact]
    public void XmlNormalizationPreservesExplicitFragmentNamespacesAndContent()
    {
        var response = new CellStorageResponse { Responses = { new() { SubResponses =
        {
            new() { SubResponseDataXml = "<m:value xmlns:m='urn:metadata'>a &amp; b</m:value>" },
            new() { SubResponseXml = "<r:Results xmlns:r='urn:results'><r:Result>value</r:Result></r:Results>" },
        } } } };
        var message = response.PrepareMtomMessage();
        var part = Assert.Single(MtomMessageParser.Parse(message.ToArray(), message.ContentType));
        var xml = XDocument.Parse(Encoding.UTF8.GetString(part.Content));
        Assert.Equal("a & b", Assert.Single(xml.Descendants(XName.Get("value", "urn:metadata"))).Value);
        Assert.Equal("value", Assert.Single(xml.Descendants(XName.Get("Result", "urn:results"))).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedMessagePreservesXmlPrecedenceAttributesAndMultipleAttachments(bool directBody)
    {
        var response = new CellStorageResponse
        {
            UsesDirectBody = directBody, WebUrl = "https://example.test/a?b=1&c=2",
            Responses = { new() { Url = "https://example.test/a%20b.docx", SubResponses =
            {
                new() { SubResponseDataBase64 = [0, 1, 255], SubResponseDataAttributes = { ["Test"] = "<&\"" } },
                new() { SubResponseDataBase64 = [42] },
                new() { SubResponseDataBase64 = [] },
                new() { SubResponseDataXml = "AQID", SubResponseDataBase64 = [99] },
                new() { SubResponseXml = "<Results><Result>value</Result></Results>", SubResponseDataBase64 = [99] },
            } } },
        };
        var message = response.PrepareMtomMessage();
        var expected = message.ToArray();
        response.Responses.Clear();
        using var stream = new NonSeekableDestination();
        await message.WriteToAsync(stream);
        Assert.Equal(expected, stream.Bytes);
        Assert.Equal(expected.LongLength, message.ContentLength);
        Assert.True(stream.CanWrite);
        var parts = MtomMessageParser.Parse(expected, message.ContentType);
        Assert.Equal(3, parts.Count);
        Assert.Equal(new byte[] { 0, 1, 255 }, parts[1].Content);
        Assert.Equal(new byte[] { 42 }, parts[2].Content);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(parts[0].Content));
        XNamespace protocol = CellStorageRequest.Namespace;
        XNamespace xop = "http://www.w3.org/2004/08/xop/include";
        var data = xml.Descendants(protocol + "SubResponseData").ToArray();
        Assert.Equal("<&\"", (string?)data[0].Attribute("Test"));
        Assert.Equal("cid:" + parts[1].ContentId!.Trim('<', '>'), (string?)data[0].Element(xop + "Include")!.Attribute("href"));
        Assert.Empty(data[2].Elements());
        Assert.Equal("", data[2].Value);
        Assert.Equal("AQID", data[3].Value);
        Assert.Single(xml.Descendants(), e => e.Name.LocalName == "Results");
        Assert.Equal(!directBody, xml.Descendants(protocol + "ExecuteCellStorageResponse").Any());
    }

    [Fact]
    public async Task CancellationBeforeWritingLeavesDestinationUntouched()
    {
        var message = new CellStorageResponse().PrepareMtomMessage();
        using var destination = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            message.WriteToAsync(destination, new CancellationToken(true)).AsTask());
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void PreparingLargeAttachmentDoesNotAllocateAnotherPayloadOrBase64Representation()
    {
        var response = new CellStorageResponse { Responses = { new() { SubResponses =
            { new() { SubResponseDataBase64 = new byte[4 * 1024 * 1024] } } } } };
        response.PrepareMtomMessage();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var message = response.PrepareMtomMessage();
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 65536);
        Assert.True(message.ContentLength > 4 * 1024 * 1024);
    }

    private sealed class NonSeekableDestination : Stream
    {
        private readonly MemoryStream _inner = new();
        public byte[] Bytes => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
