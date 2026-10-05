"""Validate the exact CellBridge package/symbol set and release provenance."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

PROJECTS = ["FssHttpB", "Storage.Abstractions", "Storage", "FssHttp", "Storage.InMemory",
            "Storage.PostgreSql", "Storage.FileSystem", "Storage.Conformance", "AspNetCore"]


def build_manifest(output, version, source_commit, source_dirty):
    output = Path(output)
    expected = {f"CellBridge.{name}.{version}.nupkg" for name in PROJECTS}
    if {p.name for p in output.glob("*.nupkg")} != expected:
        raise RuntimeError("Packed artifacts do not match the expected package set and version.")
    if {p.name for p in output.glob("*.snupkg")} != {n.replace(".nupkg", ".snupkg") for n in expected}:
        raise RuntimeError("Every package must have its matching symbol package.")
    manifest = {"version": version, "sourceCommit": source_commit, "sourceDirty": source_dirty, "packages": []}
    for path in sorted(output.glob("*.nupkg")):
        with zipfile.ZipFile(path) as package:
            nuspec = next(n for n in package.namelist() if n.endswith(".nuspec"))
            metadata = ET.fromstring(package.read(nuspec))
            def field(name):
                return metadata.find(f".//{{*}}{name}")
            package_id = path.name.removesuffix(f".{version}.nupkg")
            if field("id").text != package_id:
                raise RuntimeError(f"Incorrect package identity in {path.name}.")
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
                symbol_nuspec = next(n for n in symbol_package.namelist() if n.endswith(".nuspec"))
                symbol_metadata = ET.fromstring(symbol_package.read(symbol_nuspec))
                symbol_repository = symbol_metadata.find(".//{*}repository")
                if symbol_metadata.findtext(".//{*}id") != package_id or symbol_metadata.findtext(".//{*}version") != version or \
                        symbol_repository is None or symbol_repository.get("commit") != source_commit:
                    raise RuntimeError(f"Symbol package identity/version/commit mismatch in {symbols.name}.")
                if not any(n.startswith("lib/net10.0/") and n.endswith(".pdb") for n in symbol_package.namelist()):
                    raise RuntimeError(f"Missing portable symbols in {symbols.name}.")
            manifest["packages"].append({"id": field("id").text, "file": path.name,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                "symbols": symbols.name, "symbolsSha256": hashlib.sha256(symbols.read_bytes()).hexdigest(),
                "repositoryCommit": repository.get("commit")})
    return manifest


def validate_release(output, version, source_commit):
    output = Path(output)
    supplied = json.loads((output / "manifest.json").read_text())
    expected = build_manifest(output, version, source_commit, False)
    if supplied.get("sourceDirty") is not False or supplied != expected:
        raise RuntimeError("Release manifest, hashes or clean source provenance do not match the artifacts.")
    return supplied
