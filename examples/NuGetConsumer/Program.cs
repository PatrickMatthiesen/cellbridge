using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

var builder = WebApplication.CreateBuilder(args);

// This example keeps documents in memory. Restarting it discards their contents.
var provider = new StorageProvider(
    new InMemoryStateStore(),
    new InMemoryContentStore());

builder.Services.AddCellBridge(provider, requireDurability: false);

var app = builder.Build();
app.MapCellBridge();
app.MapHealthChecks("/health");

var documents = app.Services.GetRequiredService<CellBridgeDocumentService>();
await documents.CreateAsync(
    "/shared/example.txt",
    Encoding.UTF8.GetBytes("Hello from CellBridge!\n"));

await app.RunAsync();
