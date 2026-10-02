"""Pack libraries, build the example and run tests using package references only."""
import pathlib
import subprocess
import shutil

root = pathlib.Path(__file__).resolve().parents[1]
output = root / "artifacts" / "packages"
output.mkdir(parents=True, exist_ok=True)
projects = ["FssHttpB", "Storage.Abstractions", "Storage", "FssHttp", "Storage.InMemory",
            "Storage.PostgreSql", "Storage.FileSystem", "Storage.Conformance", "AspNetCore"]
for name in projects:
    subprocess.run(["dotnet", "pack", str(root / "src" / ("CellBridge." + name)),
                    "-c", "Release", "-o", str(output), "--nologo", "--verbosity", "quiet"], check=True)
consumer = root / "examples" / "NuGetConsumer" / "NuGetConsumer.csproj"
tests = root / "tests" / "CellBridge.Packages.Tests" / "CellBridge.Packages.Tests.csproj"
config = output / "NuGet.Config"
shutil.rmtree(output / "consumer-cache", ignore_errors=True)
config.write_text(f'''<configuration>
  <packageSources><clear/><add key="local" value="{output}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="CellBridge.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>''')
for project in (consumer, tests):
    subprocess.run(["dotnet", "restore", str(project), "--configfile", str(config),
                    "--force", "--no-cache", "--packages", str(output / "consumer-cache")], check=True)
subprocess.run(["dotnet", "build", str(consumer), "-c", "Release", "--no-restore", "--nologo"], check=True)
subprocess.run(["dotnet", "test", str(tests), "-c", "Release", "--no-restore", "--nologo",
                "--logger", "trx", "--results-directory", str(output / "test-results")], check=True)
