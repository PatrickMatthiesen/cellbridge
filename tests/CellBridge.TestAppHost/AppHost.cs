var builder = DistributedApplication.CreateBuilder(args);
// Session lifetime and no data volume keep test data separate from developer databases.
var storage = builder.AddPostgres("test-storage").WithImageTag("18.3");
var documents = storage.AddDatabase("cellbridge");
storage.AddDatabase("lists");
builder.AddProject<Projects.CellBridge_Storage_Setup>("storage-init")
    .WithReference(documents)
    .WaitFor(documents);
builder.Build().Run();
