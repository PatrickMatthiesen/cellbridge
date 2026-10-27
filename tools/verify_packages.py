"""Pack an exact library set and validate consumers using an isolated package feed."""
import argparse
import hashlib
import json
import os
import pathlib
from package_manifest import PROJECTS, build_manifest
import shutil
import subprocess
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--sqlserver", action="store_true",
                    help="Run package-only consumers on the existing disposable Aspire SQL Server fixture.")
args = parser.parse_args()
# Keep these settings local to verification processes. Do not replace global tools
# or stop another worktree's compiler, application or database processes.
environment = dict(os.environ, DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
environment.pop("CELLBRIDGE_PACKAGE_SQLSERVER_CONNECTION", None)
build_options = ["-m:1", "-p:UseSharedCompilation=false", "-nr:false"]


def run(command, **kwargs):
    subprocess.run(command, check=True, env=environment, **kwargs)

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
    run(["dotnet", "pack", str(root / "src" / name),
         "-c", "Release", "-o", str(output), "--nologo", "--verbosity", "quiet", *build_options])
if source_commit != subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip() or \
        source_status != subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True):
    raise RuntimeError("Source changed while packing; rebuild from an unchanged checkout.")
manifest = build_manifest(output, version, source_commit, bool(source_status.strip()))
(output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
consumer = root / "examples" / "NuGetConsumer" / "NuGetConsumer.csproj"
tests = root / "tests" / "CellBridge.Packages.Tests" / "CellBridge.Packages.Tests.csproj"
library = root / "examples" / "DocumentLibrary" / "CellBridge.DocumentLibrary.csproj"
library_tests = root / "tests" / "CellBridge.DocumentLibrary.Tests" / "CellBridge.DocumentLibrary.Tests.csproj"
list_example = root / "examples" / "HostAuthorizedLists" / "HostAuthorizedLists.csproj"
core_consumer = root / "tests" / "CellBridge.Packages.Tests" / "CoreConsumer" / "CellBridge.Core.PackageConsumer.csproj"
sql_consumers = [root / "tests" / name / f"{name}.csproj" for name in
                 ("CellBridge.Storage.SqlServer.Packages.Tests", "CellBridge.Lists.SqlServer.Packages.Tests")]
projects = (consumer, tests, library, library_tests, list_example, core_consumer, *sql_consumers)
# The application and its test/probe projects may reference each other, but no
# project may bypass the packed libraries through a source project reference.
for directory in (consumer.parent, tests.parent, library.parent, library_tests.parent, list_example.parent, core_consumer.parent,
                  *(project.parent for project in sql_consumers)):
    for project in directory.rglob("*.csproj"):
        for reference in ET.parse(project).iter("ProjectReference"):
            target = (project.parent / reference.attrib["Include"]).resolve()
            if target.is_relative_to(root / "src"):
                raise RuntimeError(f"Package consumer has a source reference: {project.relative_to(root)}")
            if directory in (consumer.parent, tests.parent, *(project.parent for project in sql_consumers)):
                raise RuntimeError(f"Independent package consumer has a project reference: {project.relative_to(root)}")
config = output / "NuGet.Config"
config.write_text(f'''<configuration>
  <packageSources><clear/><add key="local" value="{escape(str(output), {'"': '&quot;'})}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="CellBridge"/><package pattern="CellBridge.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>''')
for project in projects:
    # A previous build must not supply an unreferenced CellBridge assembly.
    for folder in ("bin", "obj"):
        shutil.rmtree(project.parent / folder, ignore_errors=True)
for project in projects:
    run(["dotnet", "restore", str(project), "--configfile", str(config),
         "--force", "--no-cache", "--packages", str(output / "consumer-cache"), *build_options])
for project in (consumer, library, list_example, core_consumer, *sql_consumers):
    run(["dotnet", "build", str(project), "-c", "Release", "--no-restore", "--nologo", *build_options])
for project, results in ((tests, "test-results"), (library_tests, "document-library-tests")):
    run(["dotnet", "test", str(project), "-c", "Release", "--no-restore", "--nologo", *build_options,
         "--logger", "trx", "--results-directory", str(output / results)])

core_assets = json.loads((core_consumer.parent / "obj" / "project.assets.json").read_text())
framework_references = core_assets["project"]["frameworks"]["net11.0"].get("frameworkReferences", {})
if "Microsoft.AspNetCore.App" in framework_references:
    raise RuntimeError("The CellBridge core package requires the ASP.NET Core shared framework.")
core_libraries = core_assets["libraries"]
if "CellBridge/" + version not in core_libraries or "CellBridge.FssHttpB/" + version not in core_libraries:
    raise RuntimeError("The core consumer did not restore CellBridge and its binary codec from packages.")
if any(name.startswith("CellBridge.AspNetCore/") or name.startswith("CellBridge.Storage.")
       for name in core_libraries):
    raise RuntimeError("The CellBridge core package depends on a host adapter or storage provider.")

# Check the complete restored graph and copied runtime assemblies. A package-only
# csproj alone cannot detect stale DLLs or an unintended cross-domain dependency.
identities = []
for project in sql_consumers:
    assets = json.loads((project.parent / "obj" / "project.assets.json").read_text())
    if {pathlib.Path(path).resolve() for path in assets["packageFolders"]} != {(output / "consumer-cache").resolve()}:
        raise RuntimeError(f"Consumer restored outside the isolated package cache: {project.name}")
    libraries = assets["libraries"]
    cellbridge = {name: library for name, library in libraries.items()
                  if name.split("/", 1)[0] == "CellBridge" or name.startswith("CellBridge.")}
    lists_only = project.stem.startswith("CellBridge.Lists.")
    for name, library_info in cellbridge.items():
        package_id, resolved_version = name.split("/")
        if library_info["type"] != "package" or resolved_version != version:
            raise RuntimeError(f"Consumer has a source or wrong-version dependency: {name}")
        if (lists_only and package_id not in ("CellBridge.Lists", "CellBridge.Lists.SqlServer")) or \
                (not lists_only and package_id.startswith("CellBridge.Lists")):
            raise RuntimeError(f"Document/Lists dependency boundary violated: {name}")
    if any("npgsql" in name.lower() or "postgresql" in name.lower() for name in libraries):
        raise RuntimeError(f"SQL Server consumer depends on PostgreSQL: {project.name}")
    binaries = []
    for name in sorted(cellbridge):
        package_id = name.split("/")[0]
        with zipfile.ZipFile(output / f"{package_id}.{version}.nupkg") as package:
            for member in package.namelist():
                if member.startswith("lib/net11.0/") and member.endswith(".dll"):
                    filename = pathlib.Path(member).name
                    copied = project.parent / "bin" / "Release" / "net11.0" / filename
                    sha256 = hashlib.sha256(package.read(member)).hexdigest()
                    if not copied.is_file() or hashlib.sha256(copied.read_bytes()).hexdigest() != sha256:
                        raise RuntimeError(f"Consumer assembly differs from packed assembly: {copied}")
                    binaries.append({"package": package_id, "assembly": filename, "sha256": sha256})
    actual = {p.name for p in (project.parent / "bin" / "Release" / "net11.0").glob("CellBridge*.dll")
              if p.stem != project.stem}
    if actual != {entry["assembly"] for entry in binaries}:
        raise RuntimeError(f"Consumer contains unexpected CellBridge assemblies: {project.name}")
    assembly = project.parent / "bin" / "Release" / "net11.0" / f"{project.stem}.dll"
    identities.append({"name": project.stem, "assembly": str(assembly),
                       "sha256": hashlib.sha256(assembly.read_bytes()).hexdigest(),
                       "sourceCommit": source_commit, "sourceDirty": bool(source_status.strip()),
                       "dependencies": sorted(libraries), "binaries": binaries})
(output / "consumers.json").write_text(json.dumps(identities, indent=2) + "\n")

if args.sqlserver:
    environment["CELLBRIDGE_SQLSERVER_PACKAGE_OUTPUT"] = str(output)
    run(["dotnet", "test", str(root / "tests" / "CellBridge.SqlServer.Tests"), "-c", "Release", "--nologo",
         *build_options, "--filter", "FullyQualifiedName~SqlServerPackageConsumerTests",
         "--logger", "trx", "--results-directory", str(output / "sqlserver-orchestration")])
    for identity in identities:
        results = ET.parse(output / "sqlserver-tests" / identity["name"] / "results.trx")
        counters = results.find(".//{*}Counters")
        if counters is None or any(counters.get(key) != "2" for key in ("total", "executed", "passed")):
            raise RuntimeError(f"Expected both package checks to pass without skips: {identity['name']}")
else:
    for project in sql_consumers:
        run(["dotnet", "test", str(project), "-c", "Release", "--no-build", "--no-restore", "--nologo",
             "--logger", "trx", "--results-directory", str(output / "sqlserver-tests" / project.stem)])
    print("SQL Server database cases skipped; use --sqlserver for the owned disposable engine.")
if source_commit != subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip() or \
        source_status != subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True):
    raise RuntimeError("Source changed during consumer verification; rerun from an unchanged checkout.")
summary = {"sourceCommit": source_commit, "sourceDirty": bool(source_status.strip()), "version": version,
           "sqlServerRequested": args.sqlserver, "results": []}
for path in sorted(output.rglob("*.trx")):
    counters = ET.parse(path).find(".//{*}Counters")
    summary["results"].append({"path": str(path.relative_to(output)),
                               "counters": counters.attrib if counters is not None else None})
(output / "verification.json").write_text(json.dumps(summary, indent=2) + "\n")
print(f"Verified {len(PROJECTS)} packages at {version}; manifest: {output / 'manifest.json'}")
