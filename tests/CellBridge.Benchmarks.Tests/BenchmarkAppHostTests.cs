using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace CellBridge.Benchmarks.Tests;

public sealed class BenchmarkAppHostTests
{
    [Fact]
    public async Task BenchmarkGraphHasEphemeralDatabaseAndRequiresExplicitWorkloadStart()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CellBridge_Benchmark_AppHost>();
        var postgres = Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        Assert.Empty(postgres.Annotations.OfType<ContainerMountAnnotation>());
        var workloads = builder.Resources.OfType<ProjectResource>().ToArray();
        Assert.Equal(new[] { "lists-benchmark", "storage-benchmark" }, workloads.Select(r => r.Name).Order().ToArray());
        Assert.All(workloads, r =>
        {
            Assert.Single(r.Annotations.OfType<ExplicitStartupAnnotation>());
            var dependency = Assert.Single(r.Annotations.OfType<WaitAnnotation>(), a => a.Resource is PostgresDatabaseResource);
            Assert.IsType<PostgresDatabaseResource>(dependency.Resource);
            Assert.Contains(r.Annotations.OfType<ResourceRelationshipAnnotation>(), a => a.Type == "Reference" && a.Resource == dependency.Resource);
        });
        Assert.Equal(new[] { "cellbridge", "lists" }, builder.Resources.OfType<PostgresDatabaseResource>().Select(r => r.Name).Order().ToArray());
    }
}
