using OfficeCollabServer.FssHttpB;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/OfficeCollabServer.FssHttpB.Dump -- <payload> [...]");
    return 2;
}

foreach (var path in args)
{
    Console.WriteLine($"=== {path} ===");
    try
    {
        Console.WriteLine(FsshttpbResponseInspector.Inspect(File.ReadAllBytes(path)).ToCanonicalText());
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 1;
    }
}

return 0;
