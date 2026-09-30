using System.Text;
using CellBridge.FssHttp;

namespace CellBridge.FssHttp.Tests;

public sealed class MtomBoundaryTests
{
    [Fact]
    public void BoundaryLikeBytesInsideBinaryAttachmentArePreserved()
    {
        byte[] payload = [0, 255, ..Encoding.ASCII.GetBytes("inline--capture\r\n--capture-not-a-delimiter\r\n"), 128];
        byte[] message = [..Encoding.ASCII.GetBytes("--capture\r\nContent-Type: application/octet-stream\r\nContent-ID: <binary>\r\n\r\n"),
            ..payload, ..Encoding.ASCII.GetBytes("\r\n--capture--\r\n")];

        var parts = MtomMessageParser.Parse(message, "multipart/related; boundary=capture");

        Assert.Equal(payload, Assert.Single(parts).Content);
    }

    [Fact]
    public void DuplicateContentIdsAreRejectedInsteadOfResolvingArbitrarily()
    {
        var message = Encoding.ASCII.GetBytes("--capture\r\nContent-ID: <duplicate>\r\n\r\na\r\n" +
            "--capture\r\nContent-ID: <duplicate>\r\n\r\nb\r\n--capture--\r\n");
        Assert.Throws<InvalidDataException>(() => MtomMessageParser.Parse(message, "multipart/related; boundary=capture"));
    }

    [Fact]
    public void TruncatedAttachmentIsRejected()
    {
        var message = Encoding.ASCII.GetBytes("--capture\r\nContent-ID: <binary>\r\n\r\nbody--capture");
        Assert.Throws<InvalidDataException>(() => MtomMessageParser.Parse(message, "multipart/related; boundary=capture"));
    }
}
