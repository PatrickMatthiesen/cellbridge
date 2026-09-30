using System.IO.Compression;
using System.Text;

namespace CellBridge.Web;

/// <summary>
/// Creates a minimal but valid .docx (OOXML WordprocessingML package) so that
/// Word can open the seeded document without rejecting it as corrupt.
/// </summary>
public static class MinimalDocx
{
    /// <summary>
    /// Builds a minimal DOCX containing a single paragraph of text.
    /// A DOCX is an OPC package: a ZIP with [Content_Types].xml, _rels/.rels,
    /// and word/document.xml (+ its relationship entry).
    /// </summary>
    public static byte[] Create(string text = "Hello from CellBridge!")
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // [Content_Types].xml — declares the package part types.
            var contentTypes = zip.CreateEntry("[Content_Types].xml");
            using (var w = new StreamWriter(contentTypes.Open(), new UTF8Encoding(false)))
            {
                w.Write("""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                      <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                      <Default Extension="xml" ContentType="application/xml"/>
                      <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                      <Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>
                    </Types>
                    """);
            }

            // _rels/.rels — points at the main document part.
            var rels = zip.CreateEntry("_rels/.rels");
            using (var w = new StreamWriter(rels.Open(), new UTF8Encoding(false)))
            {
                w.Write("""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                    </Relationships>
                    """);
            }

            // word/document.xml — one paragraph with the given text.
            var document = zip.CreateEntry("word/document.xml");
            using (var w = new StreamWriter(document.Open(), new UTF8Encoding(false)))
            {
                w.Write("""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                      <w:body>
                        <w:p>
                          <w:r>
                            <w:t>
                    """);
                w.Write(System.Security.SecurityElement.Escape(text));
                w.Write("""
                            </w:t>
                          </w:r>
                        </w:p>
                        <w:sectPr>
                          <w:pgSz w:w="11906" w:h="16838"/>
                          <w:pgMar w:top="1417" w:right="1417" w:bottom="1417" w:left="1417"/>
                        </w:sectPr>
                      </w:body>
                    </w:document>
                    """);
            }

            // word/_rels/document.xml.rels — links the settings part to the document.
            var docRels = zip.CreateEntry("word/_rels/document.xml.rels");
            using (var w = new StreamWriter(docRels.Open(), new UTF8Encoding(false)))
            {
                w.Write("""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/>
                    </Relationships>
                    """);
            }

            // word/settings.xml — declares compatibilityMode=15 (Word 2013+).
            // Without a compatibilityMode, Word defaults to the oldest mode and
            // shows the "Compatibility Mode" banner in the title bar.
            var settings = zip.CreateEntry("word/settings.xml");
            using (var w = new StreamWriter(settings.Open(), new UTF8Encoding(false)))
            {
                w.Write("""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                      <w:compat>
                        <w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="15"/>
                      </w:compat>
                    </w:settings>
                    """);
            }
        }

        return stream.ToArray();
    }
}
