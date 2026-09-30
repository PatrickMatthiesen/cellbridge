using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class PutChangesResponseTests
{
    [Fact]
    public void CapturedSharePointSaveResponse_ParsesPutChangesSequenceAndRoundTrips()
    {
        byte[] bytes = ExtractBinaryPart();

        var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(bytes));

        var sub = Assert.Single(response.SubResponses);
        var data = Assert.IsType<PutChangesSubResponseData>(sub.Data);
        var put = Assert.IsType<PutChangesResponse>(data.PutChangesResponse);
        Assert.True(put.AppliedStorageIndexID.IsNull);
        Assert.Equal(17, put.DataElementAdded.Count);
        Assert.Equal(29, put.TrailingPayloadBytes.Length);
        Assert.NotNull(data.KnowledgeBytes);
        Assert.True(data.KnowledgeBytes!.Length > 0);
        Assert.Null(data.DiagnosticRequestOptionOutput);

        var roundTrip = response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);
        Assert.Equal(bytes, roundTrip);
    }

    private static byte[] ExtractBinaryPart()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-first.json");
        using var document = JsonDocument.Parse(File.ReadAllText(fixture));
        string base64 = document.RootElement.GetProperty("response").GetProperty("bodyBase64").GetString()!;
        string raw = Encoding.Latin1.GetString(Convert.FromBase64String(base64));
        var match = Regex.Match(raw,
            "Content-ID: <http://tempuri.org/(\\d+)/[^\\r\\n]+\\r?\\n" +
            "Content-Transfer-Encoding: binary\\r?\\n" +
            "Content-Type: application/octet-stream\\r?\\n\\r?\\n" +
            "(?<bytes>.*?)\\r?\\n--",
            RegexOptions.Singleline);
        Assert.True(match.Success, "The fixture did not contain a binary FSSHTTPB MIME part.");
        return Encoding.Latin1.GetBytes(match.Groups["bytes"].Value);
    }
}
