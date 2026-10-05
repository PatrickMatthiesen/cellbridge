"""Pack an exact library set and validate consumers using an isolated package feed."""
import hashlib
import json
import pathlib
import shutil
import subprocess
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
output = root / "artifacts" / "packages"
# Only this script-owned, ignored output is cleared; stale packages cannot mask a missing build.
shutil.rmtree(output, ignore_errors=True)
output.mkdir(parents=True)
version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
if not version or "-beta." not in version:
    raise RuntimeError("Package verification expects an explicit beta version.")
projects = ["FssHttpB", "Storage.Abstractions", "Storage", "FssHttp", "Storage.InMemory",
            "Storage.PostgreSql", "Storage.FileSystem", "Storage.Conformance", "AspNetCore"]
for name in projects:
    subprocess.run(["dotnet", "pack", str(root / "src" / ("CellBridge." + name)),
                    "-c", "Release", "-o", str(output), "--nologo", "--verbosity", "quiet"], check=True)
expected = {f"CellBridge.{name}.{version}.nupkg" for name in projects}
if {p.name for p in output.glob("*.nupkg")} != expected:
    raise RuntimeError("Packed artifacts do not match the expected package set and version.")
if {p.name for p in output.glob("*.snupkg")} != {n.replace(".nupkg", ".snupkg") for n in expected}:
    raise RuntimeError("Every package must have its matching symbol package.")
source_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
source_dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True).strip())
manifest = {"version": version, "sourceCommit": source_commit, "sourceDirty": source_dirty, "packages": []}
for path in sorted(output.glob("*.nupkg")):
    with zipfile.ZipFile(path) as package:
        nuspec = next(n for n in package.namelist() if n.endswith(".nuspec"))
        metadata = ET.fromstring(package.read(nuspec))
        def field(name):
            return metadata.find(f".//{{*}}{name}")
        if field("version").text != version or field("license").text != "MIT":
            raise RuntimeError(f"Incorrect version or license in {path.name}.")
        if field("readme").text != "README.md" or "README.md" not in package.namelist():
            raise RuntimeError(f"Missing package readme in {path.name}.")
        repository = field("repository")
        if repository is None or repository.get("url") != "https://github.com/PatrickMatthiesen/cellbridge":
            raise RuntimeError(f"Missing repository metadata in {path.name}.")
        if repository.get("commit") != source_commit:
            raise RuntimeError(f"Repository commit mismatch in {path.name}.")
        for dependency in metadata.findall(".//{*}dependency"):
            if dependency.get("id", "").startswith("CellBridge.") and dependency.get("version") != version:
                raise RuntimeError(f"Inconsistent internal dependency version in {path.name}.")
        if not any(n.startswith("lib/net10.0/") and n.endswith(".dll") for n in package.namelist()):
            raise RuntimeError(f"Missing .NET 10 assembly in {path.name}.")
        symbols = path.with_suffix(".snupkg")
        with zipfile.ZipFile(symbols) as symbol_package:
            if not any(n.startswith("lib/net10.0/") and n.endswith(".pdb") for n in symbol_package.namelist()):
                raise RuntimeError(f"Missing portable symbols in {symbols.name}.")
        manifest["packages"].append({"id": field("id").text, "file": path.name,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "symbols": symbols.name, "symbolsSha256": hashlib.sha256(symbols.read_bytes()).hexdigest(),
            "repositoryCommit": repository.get("commit")})
(output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
consumer = root / "examples" / "NuGetConsumer" / "NuGetConsumer.csproj"
tests = root / "tests" / "CellBridge.Packages.Tests" / "CellBridge.Packages.Tests.csproj"
config = output / "NuGet.Config"
config.write_text(f'''<configuration>
  <packageSources><clear/><add key="local" value="{escape(str(output), {'"': '&quot;'})}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="CellBridge.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>''')
for project in (consumer, tests):
    subprocess.run(["dotnet", "restore", str(project), "--configfile", str(config),
                    "--force", "--no-cache", "--packages", str(output / "consumer-cache")], check=True)
subprocess.run(["dotnet", "build", str(consumer), "-c", "Release", "--no-restore", "--nologo"], check=True)
subprocess.run(["dotnet", "test", str(tests), "-c", "Release", "--no-restore", "--nologo",
                "--logger", "trx", "--results-directory", str(output / "test-results")], check=True)
print(f"Verified {len(expected)} packages at {version}; manifest: {output / 'manifest.json'}")
