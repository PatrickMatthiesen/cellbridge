"""Pack an exact library set and validate consumers using an isolated package feed."""
import json
import pathlib
from package_manifest import PROJECTS, build_manifest
import shutil
import subprocess
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

root = pathlib.Path(__file__).resolve().parents[1]
output = root / "artifacts" / "packages"
# Only this script-owned, ignored output is cleared; stale packages cannot mask a missing build.
shutil.rmtree(output, ignore_errors=True)
output.mkdir(parents=True)
version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
if not version or "-beta." not in version:
    raise RuntimeError("Package verification expects an explicit beta version.")
source_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
source_status = subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True)
for name in PROJECTS:
    subprocess.run(["dotnet", "pack", str(root / "src" / ("CellBridge." + name)),
                    "-c", "Release", "-o", str(output), "--nologo", "--verbosity", "quiet"], check=True)
if source_commit != subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip() or \
        source_status != subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True):
    raise RuntimeError("Source changed while packing; rebuild from an unchanged checkout.")
manifest = build_manifest(output, version, source_commit, bool(source_status.strip()))
(output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
consumer = root / "examples" / "NuGetConsumer" / "NuGetConsumer.csproj"
tests = root / "tests" / "CellBridge.Packages.Tests" / "CellBridge.Packages.Tests.csproj"
library = root / "examples" / "DocumentLibrary" / "CellBridge.DocumentLibrary.csproj"
library_setup = library.parent / "Setup" / "CellBridge.DocumentLibrary.Setup.csproj"
library_tests = root / "tests" / "CellBridge.DocumentLibrary.Tests" / "CellBridge.DocumentLibrary.Tests.csproj"
# The application and its test/probe projects may reference each other, but no
# project may bypass the packed libraries through a source project reference.
for directory in (library.parent, library_tests.parent):
    for project in directory.rglob("*.csproj"):
        for reference in ET.parse(project).iter("ProjectReference"):
            target = (project.parent / reference.attrib["Include"]).resolve()
            if target.is_relative_to(root / "src"):
                raise RuntimeError(f"Package consumer has a source reference: {project.relative_to(root)}")
config = output / "NuGet.Config"
config.write_text(f'''<configuration>
  <packageSources><clear/><add key="local" value="{escape(str(output), {'"': '&quot;'})}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="CellBridge.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>''')
for project in (consumer, tests, library, library_setup, library_tests):
    subprocess.run(["dotnet", "restore", str(project), "--configfile", str(config),
                    "--force", "--no-cache", "--packages", str(output / "consumer-cache")], check=True)
subprocess.run(["dotnet", "build", str(consumer), "-c", "Release", "--no-restore", "--nologo"], check=True)
subprocess.run(["dotnet", "build", str(library_setup), "-c", "Release", "--no-restore", "--nologo"], check=True)
subprocess.run(["dotnet", "test", str(tests), "-c", "Release", "--no-restore", "--nologo",
                "--logger", "trx", "--results-directory", str(output / "test-results")], check=True)
subprocess.run(["dotnet", "test", str(library_tests), "-c", "Release", "--no-restore", "--nologo",
                "--logger", "trx", "--results-directory", str(output / "document-library-tests")], check=True)
print(f"Verified {len(PROJECTS)} packages at {version}; manifest: {output / 'manifest.json'}")
