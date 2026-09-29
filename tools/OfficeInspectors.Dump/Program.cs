using OfficeInspectors.Adapter;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/OfficeInspectors.Dump -- <payload> [...]");
    return 2;
}

foreach (var path in args)
{
    var result = OfficeInspectorsAdapter.ParseResponse(File.ReadAllBytes(path));
    Console.WriteLine($"=== {path} ===");
    Console.WriteLine(result.Summary);
    if (result.Error is not null)
        Console.WriteLine(result.Error);
}

return 0;
