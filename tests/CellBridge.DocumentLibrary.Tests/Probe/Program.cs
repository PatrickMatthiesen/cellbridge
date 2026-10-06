using System.Security.Cryptography;
using CellBridge.DocumentLibrary;
using CellBridge.Storage.Abstractions;

if (args.Length != 7)
    throw new ArgumentException("Expected root, resource ID, binding ID, expected revision, operation ID, input file, and signal file.");

var root = args[0];
var resourceId = Guid.Parse(args[1]);
var bindingId = Guid.Parse(args[2]);
var expectedRevision = args[3];
var operationId = Guid.Parse(args[4]);
var inputFile = args[5];
var signalFile = args[6];
var bytes = await File.ReadAllBytesAsync(inputFile);
var handle = new ContentHandle("probe-" + operationId.ToString("N"), bytes.LongLength,
    Convert.ToHexStringLower(SHA256.HashData(bytes)));
var revision = new ExternalRevision(operationId, resourceId, 1, 1, 2, handle);
var request = new ExternalDeliveryRequest(bindingId, resourceId.ToString("D"), expectedRevision, revision);
await using var destination = new DocumentLibraryDestination(root, new SignalAndWaitDestinationFaults(signalFile));
await using var content = new MemoryStream(bytes, writable: false);
await destination.CompareExchangeAsync(request, content);
