using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OfficeCollabServer.FssHttp;

namespace OfficeCollabServer.FssHttp.Tests;

public sealed class MtomResponseFramingTests
{
    [Theory]
    [InlineData("open")]
    [InlineData("save-first")]
    [InlineData("save-second")]
    [InlineData("reopen")]
    public void RootPartPreambleAndHeadersMatchSuccessfulSharePointResponse(string name)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
        var source = fixture.RootElement.GetProperty("response");
        var reference = Convert.FromBase64String(source.GetProperty("bodyBase64").GetString()!);
        var generated = new CellStorageResponse().ToMtomMessage();
        Assert.Equal(RootHeaders(reference, source.GetProperty("contentType").GetString()!),
            RootHeaders(generated.Body, generated.ContentType));
    }

    private static string RootHeaders(byte[] bytes, string contentType)
    {
        var media = MediaTypeHeaderValue.Parse(contentType);
        var boundary = media.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        var text = Encoding.UTF8.GetString(bytes);
        return text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)]
            .Replace(boundary, "BOUNDARY", StringComparison.Ordinal);
    }
}
