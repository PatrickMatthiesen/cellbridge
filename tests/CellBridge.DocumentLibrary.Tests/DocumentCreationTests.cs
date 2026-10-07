using System.Net;
using System.Text.RegularExpressions;
using CellBridge.AspNetCore;
using CellBridge.DocumentLibrary;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class DocumentCreationTests
{
    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public async Task CreateFromTheLibraryProducesAValidBoundAndDeliveredBlankFile(string type)
    {
        using var files = new TemporaryDirectory();
        await using var factory = new OfficeAuthenticationTests.LibraryFactory(files.Path);
        using var owner = OfficeAuthenticationTests.Client(factory);
        using var login = await OfficeAuthenticationTests.SignIn(owner, "/auth/login", "ofba-operator", "/");
        using var home = await owner.GetAsync("/");
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("New document", html);
        Assert.Contains("/library/create", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        using var missingCsrf = await owner.PostAsync("/library/create", CreateForm("Budget & plan", type));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        using var created = await owner.PostAsync("/library/create", CreateForm("Budget & plan", type, token));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var library = factory.Services.GetRequiredService<DocumentLibraryService>();
        var row = Assert.Single(await library.ListAsync(DocumentLibraryHarness.Owner));
        Assert.Equal("Budget & plan." + type, row.FileName);
        var service = factory.Services.GetRequiredService<CellBridgeDocumentService>();
        var state = await service.Provider.State.FindByResourceIdAsync(row.ResourceId);
        Assert.NotNull(state);
        Assert.NotNull(state.Publication);
        Assert.NotNull(state.Security.AuthorizationPolicy);
        await using var content = await library.OpenDeliveredAsync(row.ResourceId, DocumentLibraryHarness.Owner);
        using var bytes = new MemoryStream(); await content.CopyToAsync(bytes); bytes.Position = 0;
        using OpenXmlPackage package = type switch
        {
            "docx" => WordprocessingDocument.Open(bytes, false),
            "xlsx" => SpreadsheetDocument.Open(bytes, false),
            "pptx" => PresentationDocument.Open(bytes, false),
            _ => throw new InvalidOperationException(),
        };
        Assert.Empty(new OpenXmlValidator().Validate(package));
        using var duplicate = await owner.PostAsync("/library/create", CreateForm("Budget & plan", type, token));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Single(await library.ListAsync(DocumentLibraryHarness.Owner));
    }

    [Fact]
    public async Task ReaderCannotCreateAndProductionLoginIsUnavailable()
    {
        using var files = new TemporaryDirectory();
        await using var factory = new OfficeAuthenticationTests.LibraryFactory(files.Path);
        using var reader = OfficeAuthenticationTests.Client(factory);
        using var login = await OfficeAuthenticationTests.SignIn(reader, "/auth/login", "reader", "/");
        var html = await reader.GetStringAsync("/");
        Assert.DoesNotContain("/library/create", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        using var denied = await reader.PostAsync("/library/create", CreateForm("Forbidden", "docx", token));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<DocumentLibraryService>().ListAsync(DocumentLibraryHarness.Owner));
        // Exercise the real route gate without connecting the test host to a production database.
        factory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName = Environments.Production;
        using var get = await reader.GetAsync("/auth/login");
        using var post = await reader.PostAsync("/auth/login", CreateForm("ignored", "docx"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Theory]
    [InlineData("../escape", "docx")]
    [InlineData("CON.docx", "docx")]
    [InlineData("COM1.extra.docx", "docx")]
    [InlineData("drive:name", "docx")]
    [InlineData("wild*card", "xlsx")]
    [InlineData("name?", "pptx")]
    [InlineData("wrong.xlsx", "docx")]
    [InlineData("trailing.", "docx")]
    [InlineData("name", "pdf")]
    public async Task InvalidNewDocumentNamesNeverCreateADestination(string name, string type)
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Library.CreateBlankAsync(name, type, DocumentLibraryHarness.Owner));
        Assert.Empty(await harness.Destination.ListAsync());
    }

    private static FormUrlEncodedContent CreateForm(string name, string type, string? token = null) => new(
        new Dictionary<string, string> { ["name"] = name, ["type"] = type, ["__RequestVerificationToken"] = token ?? "" });
}
