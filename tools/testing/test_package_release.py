import json
from pathlib import Path
import sys
import zipfile
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from package_manifest import PROJECTS, build_manifest, validate_release

VERSION = "0.1.0-beta.1"
COMMIT = "a" * 40


def artifacts(path):
    for name in PROJECTS:
        package_id = "CellBridge." + name
        metadata = f'''<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata><id>{package_id}</id><version>{VERSION}</version>
          <license type="expression">MIT</license><readme>README.md</readme>
          <repository type="git" url="https://github.com/PatrickMatthiesen/cellbridge" commit="{COMMIT}"/>
          <dependencies><group targetFramework="net10.0"><dependency id="CellBridge.Storage" version="{VERSION}"/></group></dependencies>
          </metadata></package>'''
        for extension, binary in (("nupkg", "dll"), ("snupkg", "pdb")):
            with zipfile.ZipFile(path / f"{package_id}.{VERSION}.{extension}", "w") as z:
                z.writestr(package_id + ".nuspec", metadata)
                z.writestr(f"lib/net10.0/{package_id}.{binary}", b"fixture")
                if extension == "nupkg":
                    z.writestr("README.md", "fixture package")
    (path / "manifest.json").write_text(json.dumps(build_manifest(path, VERSION, COMMIT, False)))


def change_archive(path, transform):
    with zipfile.ZipFile(path) as z:
        files = {name: z.read(name) for name in z.namelist()}
    transform(files)
    with zipfile.ZipFile(path, "w") as z:
        for name, data in files.items():
            z.writestr(name, data)


def test_exact_release_artifacts_pass(tmp_path):
    artifacts(tmp_path)
    assert len(validate_release(tmp_path, VERSION, COMMIT)["packages"]) == 9


@pytest.mark.parametrize("field,value", [("sourceDirty", True), ("sourceCommit", "b" * 40), ("version", "0.1.0-beta.2")])
def test_modified_provenance_fails(tmp_path, field, value):
    artifacts(tmp_path)
    path = tmp_path / "manifest.json"
    manifest = json.loads(path.read_text()); manifest[field] = value
    path.write_text(json.dumps(manifest))
    with pytest.raises(RuntimeError):
        validate_release(tmp_path, VERSION, COMMIT)


@pytest.mark.parametrize("extension", ["nupkg", "snupkg"])
def test_missing_package_or_symbols_fails(tmp_path, extension):
    artifacts(tmp_path)
    next(tmp_path.glob("*." + extension)).unlink()
    with pytest.raises(RuntimeError):
        validate_release(tmp_path, VERSION, COMMIT)


def test_unexpected_package_fails(tmp_path):
    artifacts(tmp_path)
    (tmp_path / "unreviewed.nupkg").write_bytes(b"unexpected")
    with pytest.raises(RuntimeError):
        validate_release(tmp_path, VERSION, COMMIT)


@pytest.mark.parametrize("extension,binary", [("nupkg", "dll"), ("snupkg", "pdb")])
def test_changed_payload_fails_even_with_unchanged_archive_metadata(tmp_path, extension, binary):
    artifacts(tmp_path)
    change_archive(next(tmp_path.glob("*." + extension)),
        lambda files: files.update({next(n for n in files if n.endswith("." + binary)): b"changed"}))
    with pytest.raises(RuntimeError):
        validate_release(tmp_path, VERSION, COMMIT)


@pytest.mark.parametrize("old,new", [(COMMIT, "b" * 40), (VERSION, "0.1.0-beta.2"), ("MIT", "GPL-3.0"), ("CellBridge.FssHttpB</id>", "CellBridge.Other</id>")])
def test_invalid_archive_metadata_fails(tmp_path, old, new):
    artifacts(tmp_path)
    target = tmp_path / f"CellBridge.FssHttpB.{VERSION}.nupkg"
    change_archive(target, lambda files: files.update({next(n for n in files if n.endswith(".nuspec")):
        next(data for name, data in files.items() if name.endswith(".nuspec")).replace(old.encode(), new.encode())}))
    with pytest.raises(RuntimeError):
        build_manifest(tmp_path, VERSION, COMMIT, False)


def test_wrong_expected_commit_fails(tmp_path):
    artifacts(tmp_path)
    with pytest.raises(RuntimeError):
        validate_release(tmp_path, VERSION, "b" * 40)


def test_uploads_primaries_before_symbols_and_discovers_symbol_endpoint():
    from types import SimpleNamespace
    from publish_packages import push_packages
    calls = []
    push_packages("packages", VERSION, "temporary-fixture-key",
        lambda command: (calls.append(command), SimpleNamespace(returncode=0))[1])
    assert len(calls) == 18
    assert [Path(c[3]).name for c in calls[:9]] == [f"CellBridge.{n}.{VERSION}.nupkg" for n in PROJECTS]
    assert [Path(c[3]).name for c in calls[9:]] == [f"CellBridge.{n}.{VERSION}.snupkg" for n in PROJECTS]
    assert all("--no-symbols" in c for c in calls[:9])
    assert all("--no-symbols" not in c for c in calls[9:])
    assert all("--skip-duplicate" not in c for c in calls)


@pytest.mark.parametrize("failure_at", [0, 8, 9, 17])
def test_upload_failure_stops_without_retry_or_secret_in_exception(failure_at):
    from types import SimpleNamespace
    from publish_packages import push_packages
    calls = []
    def upload(command):
        calls.append(command)
        return SimpleNamespace(returncode=1 if len(calls) - 1 == failure_at else 0)
    with pytest.raises(SystemExit) as error:
        push_packages("packages", VERSION, "temporary-fixture-key", upload)
    assert len(calls) == failure_at + 1
    assert "temporary-fixture-key" not in str(error.value)
    assert "Publishing stopped at CellBridge." in str(error.value)
