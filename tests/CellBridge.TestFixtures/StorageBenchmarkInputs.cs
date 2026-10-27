using System.IO.Compression;

namespace CellBridge.TestFixtures;

public static class StorageBenchmarkInputs
{
    public static byte[] Package(int sizeMiB, int seed)
    {
        var random = new Random(seed);
        using var initial = new MemoryStream(MinimalDocx.Create());
        using var existing = new ZipArchive(initial, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in existing.Entries)
            {
                var copy = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                copy.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var target = copy.Open();
                if (entry.FullName == "word/document.xml")
                {
                    using var writer = new StreamWriter(target);
                    writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>");
                    writer.Write(seed.ToString("X8"));
                    var buffer = new byte[4096];
                    for (long count = 0; count < sizeMiB * 1024L * 1024; count += buffer.Length * 2)
                    {
                        random.NextBytes(buffer);
                        writer.Write(Convert.ToHexString(buffer));
                    }
                    writer.Write("</w:t></w:r></w:p></w:body></w:document>");
                }
                else
                {
                    using var input = entry.Open();
                    input.CopyTo(target);
                }
            }
        }
        return output.ToArray();
    }
}
