using CellBridge.Storage.Abstractions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace CellBridge.Client.Tests;

public sealed class DocumentSessionOfficeSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WordParagraphRoundTripsThroughDocumentSessions(bool overHttp) =>
        VerifyPackageRoundTripAsync<WordprocessingDocument>(overHttp, "/shared/client-smoke.docx",
            OfficeDocumentTemplates.CreateWordDocument(), "Word paragraph saved through CellBridge",
            WordprocessingDocument.Open,
            (document, text) => Assert.Single(document.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>())
                .AppendChild(new W.Run(new W.Text(text))),
            document => Assert.Single(document.MainDocumentPart!.Document!.Body!.Descendants<W.Text>()).Text);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PowerPointSlideTextRoundTripsThroughDocumentSessions(bool overHttp) =>
        VerifyPackageRoundTripAsync<PresentationDocument>(overHttp, "/shared/client-smoke.pptx",
            OfficeDocumentTemplates.CreatePowerPointPresentation(), "PowerPoint slide saved through CellBridge",
            PresentationDocument.Open, AddSlideText,
            document => Assert.Single(Assert.Single(document.PresentationPart!.SlideParts).Slide!.Descendants<A.Text>()).Text);

    private static void AddSlideText(PresentationDocument document, string text)
    {
        var slide = Assert.Single(document.PresentationPart!.SlideParts).Slide!;
        slide.CommonSlideData!.ShapeTree!.AppendChild(new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = 2, Name = "Smoke test text box" },
                new P.NonVisualShapeDrawingProperties { TextBox = true },
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = 914400, Y = 914400 },
                    new A.Extents { Cx = 5486400, Cy = 914400 }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text(text))))));
    }

    private static async Task VerifyPackageRoundTripAsync<TPackage>(bool overHttp, string path,
        byte[] original, string expectedText, Func<Stream, bool, TPackage> openPackage,
        Action<TPackage, string> editPackage, Func<TPackage, string> readText)
        where TPackage : OpenXmlPackage
    {
        await using var host = await DocumentSessionScenarioTests.ScenarioHost.StartAsync(overHttp, documentPath: path);
        var document = await host.CreateAsync(original);
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read);

        byte[] edited;
        string savedRevision;
        await using (var reader = await CellBridgeDocumentSession.OpenAsync(host.PeerTransport, host.DocumentUrl))
        {
            var initial = reader.GetSnapshot();
            Assert.Equal(original, initial.Package);

            var options = new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter };
            await using (var writer = await CellBridgeDocumentSession.OpenAsync(host.Transport, host.DocumentUrl, options))
            {
                var snapshot = writer.GetSnapshot();
                Assert.Equal(initial.Revision, snapshot.Revision);
                Assert.Equal(original, snapshot.Package);

                using var stream = new MemoryStream();
                stream.Write(snapshot.Package);
                stream.Position = 0;
                using (var package = openPackage(stream, true))
                    editPackage(package, expectedText);
                edited = stream.ToArray();

                var result = await writer.TryCommitAsync(snapshot.Revision, edited);
                Assert.Equal(CellBridgeCommitStatus.Committed, result.Status);
                Assert.NotEqual(initial.Revision, result.Revision);
                savedRevision = result.Revision;
            }

            AssertSavedPackage(await reader.SyncAsync());
        }

        await using var reopened = await CellBridgeDocumentSession.OpenAsync(host.PeerTransport, host.DocumentUrl);
        AssertSavedPackage(reopened.GetSnapshot());

        void AssertSavedPackage(CellBridgeDocumentSnapshot snapshot)
        {
            Assert.Equal(savedRevision, snapshot.Revision);
            Assert.Equal(edited, snapshot.Package);
            using var stream = new MemoryStream(snapshot.Package);
            using var package = openPackage(stream, false);
            var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(package);
            Assert.Empty(errors.Select(error => error.Description));
            Assert.Equal(expectedText, readText(package));
        }
    }
}
