using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeRealTimeHostingTests
{
    [Theory]
    [InlineData(null, "library")]
    [InlineData("127.0.0.1:1", null)]
    [InlineData("127.0.0.1:1", " ")]
    public async Task MultipleHostsRequireExplicitCoordinationInsteadOfDisablingRtc(string? connection, string? roomNamespace)
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var content = new PostgreSqlContentStore(source);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), content);
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddCellBridge(provider, multipleInstances: true,
            configure: options =>
            {
                options.OfficeRealTimeRedisConnectionString = connection;
                options.OfficeRealTimeRedisNamespace = roomNamespace;
            }));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(CellBridgeOptions));
    }

    [Fact]
    public async Task DefaultLocalHostDoesNotResolveRedisOrStartDistributedWorkers()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false, configure: options =>
        {
            // A single host must not parse or connect this unused configuration.
            options.OfficeRealTimeRedisConnectionString = "invalid Redis configuration";
            options.OfficeRealTimeRedisNamespace = "unused";
        });
        await using var app = builder.Build();

        Assert.True(app.Services.GetRequiredService<CellBridgeOptions>().EnableOfficeRealTime);
        Assert.IsType<OfficeRealTimeSessionStore>(app.Services.GetRequiredService<IOfficeRealTimeSessionStore>());
        Assert.Null(app.Services.GetService<IOfficeRealTimeBackplane>());
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), service => service is DistributedOfficeRealTimeSessionStore);
    }

    [Fact]
    public async Task ExplicitDisableDoesNotRegisterEitherRtcStore()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false,
            configure: options => options.EnableOfficeRealTime = false);
        await using var app = builder.Build();

        Assert.Null(app.Services.GetService<IOfficeRealTimeSessionStore>());
        Assert.Null(app.Services.GetService<IOfficeRealTimeBackplane>());
    }
}
