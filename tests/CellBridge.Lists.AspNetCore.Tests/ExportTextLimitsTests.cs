using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace CellBridge.Lists.AspNetCore.Tests;

public sealed partial class HttpTests
{
    [Theory]
    [InlineData("")]
    [InlineData("🌿")]
    [InlineData("\u0001")]
    [InlineData("_x0041_")]
    public async Task Xlsx_accepts_32767_original_utf16_units_before_xml_escaping(string prefix)
    {
        await using var host = await Host.Create();
        var value = prefix + new string('a', 32767 - prefix.Length);
        host.Engine.Record.Values[host.Engine.Text.ToString()] = value;
        var response = await host.Client.GetAsync(host.Path + "/export.xlsx?fields=" + host.Engine.Text);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var stream = new MemoryStream(bytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var worksheet = XDocument.Parse(await reader.ReadToEndAsync());
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var text = worksheet.Descendants(ns + "row").Last().Elements(ns + "c").Last().Descendants(ns + "t").Single().Value;
        var expectedPrefix = prefix switch { "\u0001" => "_x0001_", "_x0041_" => "_x005F_x0041_", _ => prefix };
        Assert.Equal(expectedPrefix + new string('a', 32767 - prefix.Length), text);
        Assert.Equal(value, host.Engine.Record.Values[host.Engine.Text.ToString()]!.GetValue<string>());
    }

    [Fact]
    public async Task Xlsx_rejects_32768_units_without_truncating_csv_or_native_values()
    {
        await using var host = await Host.Create();
        var value = new string('a', 32768);
        host.Engine.Record.Values[host.Engine.Text.ToString()] = value;
        var response = await host.Client.GetAsync(host.Path + "/export.xlsx?fields=" + host.Engine.Text);
        await AssertXlsxCellLimit(response);
        var csv = await host.Client.GetStringAsync(host.Path + "/export.csv?fields=" + host.Engine.Text);
        Assert.Contains("\"" + value + "\"", csv);
        var native = await host.Post("/records/query", "{\"query\":{\"limit\":1}}");
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        var records = JsonNode.Parse(await native.Content.ReadAsStringAsync())!["records"]!;
        Assert.Equal(value, records[0]!["values"]![host.Engine.Text.ToString()]!.GetValue<string>());
        Assert.Equal(value, host.Engine.Record.Values[host.Engine.Text.ToString()]!.GetValue<string>());
    }

    [Fact]
    public async Task Hidden_oversized_text_only_blocks_xlsx_when_explicitly_projected()
    {
        await using var host = await Host.Create();
        host.Engine.Record.Values[host.Engine.Hidden.ToString()] = new string('a', 32768);
        var defaultExport = await host.Client.GetAsync(host.Path + "/export.xlsx");
        Assert.Equal(HttpStatusCode.OK, defaultExport.StatusCode);
        var explicitExport = await host.Client.GetAsync(host.Path + "/export.xlsx?fields=" + host.Engine.Hidden);
        await AssertXlsxCellLimit(explicitExport);
    }

    [Fact]
    public async Task Xlsx_rejects_oversized_header_text_from_custom_engine()
    {
        await using var host = await Host.Create();
        host.Engine.Definition = host.Engine.Definition with
        {
            Fields = host.Engine.Definition.Fields.Select(field => field.Id == host.Engine.Text
                ? field with { Name = new string('a', 32768) } : field).ToArray()
        };
        await AssertXlsxCellLimit(await host.Client.GetAsync(host.Path + "/export.xlsx?fields=" + host.Engine.Text));
    }

    private static async Task AssertXlsxCellLimit(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("{\"error\":\"Limit\"}", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Content.Headers.ContentDisposition);
    }
}
