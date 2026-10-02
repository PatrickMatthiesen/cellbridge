"""Pack local libraries, then build and execute a consumer with no project references."""
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
config = output / "NuGet.Config"
shutil.rmtree(output / "consumer-cache", ignore_errors=True)
config.write_text(f'''<configuration>
  <packageSources><clear/><add key="local" value="{output}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="CellBridge.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>''')
subprocess.run(["dotnet", "restore", str(consumer), "--configfile", str(config),
                "--force", "--no-cache", "--packages", str(output / "consumer-cache")], check=True)
subprocess.run(["dotnet", "build", str(consumer), "-c", "Release", "--no-restore", "--nologo"], check=True)
subprocess.run(["dotnet", str(consumer.parent / "bin" / "Release" / "net10.0" / "NuGetConsumer.dll")], check=True)
