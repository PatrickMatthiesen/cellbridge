using CellBridge.OfficeInspectors;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/OfficeInspectors.Dump -- <payload> [...]");
    return 2;
}

var passed = true;
foreach (var path in args)
{
    var result = OfficeInspector.ParseResponse(File.ReadAllBytes(path));
    passed &= result.Parsed;
    Console.WriteLine(path);
    Console.WriteLine(result.Summary);
    if (result.Error is not null)
        Console.WriteLine(result.Error);
}

return passed ? 0 : 1;
