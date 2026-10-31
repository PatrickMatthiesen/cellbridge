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
    for package_id in PROJECTS:
        metadata = f'''<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata><id>{package_id}</id><version>{VERSION}</version>
          <license type="expression">MIT</license><readme>README.md</readme>
          <repository type="git" url="https://github.com/PatrickMatthiesen/cellbridge" commit="{COMMIT}"/>
          <dependencies><group targetFramework="net11.0"><dependency id="CellBridge" version="{VERSION}"/></group></dependencies>
          </metadata></package>'''
        for extension, binary in (("nupkg", "dll"), ("snupkg", "pdb")):
            with zipfile.ZipFile(path / f"{package_id}.{VERSION}.{extension}", "w") as z:
                z.writestr(package_id + ".nuspec", metadata)
                z.writestr(f"lib/net11.0/{package_id}.{binary}", b"fixture")
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
    assert len(validate_release(tmp_path, VERSION, COMMIT)["packages"]) == len(PROJECTS)


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
    assert len(calls) == 2 * len(PROJECTS)
    assert [Path(c[3]).name for c in calls[:len(PROJECTS)]] == [f"{n}.{VERSION}.nupkg" for n in PROJECTS]
    assert [Path(c[3]).name for c in calls[len(PROJECTS):]] == [f"{n}.{VERSION}.snupkg" for n in PROJECTS]
    assert all("--no-symbols" in c for c in calls[:len(PROJECTS)])
    assert all("--no-symbols" not in c for c in calls[len(PROJECTS):])
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
    assert "Publishing stopped at CellBridge" in str(error.value)


def producer():
    run = {"repository": {"full_name": "owner/repo"}, "head_repository": {"full_name": "owner/repo"},
           "workflow_id": 123, "path": ".github/workflows/publish-nuget.yml", "head_branch": "main",
           "event": "workflow_dispatch", "status": "completed", "head_sha": COMMIT, "conclusion": "failure"}
    steps = [{"name": command, "status": "completed", "conclusion": "success"} for command in
             ("Run dotnet test CellBridge.slnx", "Run python -m pytest tools/testing",
              "Run python tools/verify_packages.py", "Run python tools/check_release.py")]
    jobs = [{"name": "validate", "status": "completed", "conclusion": "success", "steps": steps},
            {"name": "publish", "status": "completed", "conclusion": "failure"}]
    return run, jobs


def test_producer_accepts_failed_publish_only():
    from verify_release_run import verify_producer
    run, jobs = producer()
    assert verify_producer(run, jobs, "owner/repo", 123) == COMMIT


@pytest.mark.parametrize("field,value", [("repository", {"full_name": "other/repo"}),
    ("head_repository", {"full_name": "fork/repo"}), ("workflow_id", 999), ("path", "other.yml"),
    ("head_branch", "feature"), ("event", "pull_request"), ("status", "in_progress"), ("head_sha", "bad")])
def test_wrong_producer_fails(field, value):
    from verify_release_run import verify_producer
    run, jobs = producer()
    run[field] = value
    with pytest.raises(RuntimeError):
        verify_producer(run, jobs, "owner/repo", 123)


@pytest.mark.parametrize("mutation", ["missing", "failed", "skipped", "incomplete", "no-tests", "failed-tests", "extra-failed"])
def test_producer_requires_all_validation(mutation):
    from verify_release_run import verify_producer
    run, jobs = producer()
    if mutation == "missing": jobs.pop(0)
    elif mutation == "no-tests": jobs[0]["steps"] = []
    elif mutation == "failed-tests": jobs[0]["steps"][0]["conclusion"] = "failure"
    elif mutation == "extra-failed": jobs.append({"name": "test-other", "status": "completed", "conclusion": "failure"})
    elif mutation == "incomplete": jobs[0]["status"] = "in_progress"
    else: jobs[0]["conclusion"] = mutation
    with pytest.raises(RuntimeError):
        verify_producer(run, jobs, "owner/repo", 123)


def signed_payload(path):
    import io
    buffer = io.BytesIO()
    with zipfile.ZipFile(path) as original, zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as signed:
        for name in reversed(original.namelist()):
            signed.writestr(name, original.read(name))
        signed.writestr(".signature.p7s", b"repository signature")
    return buffer.getvalue()


def test_resume_skips_only_identical_primary_and_retries_all_symbols(tmp_path):
    from types import SimpleNamespace
    from publish_packages import push_packages
    artifacts(tmp_path)
    first = PROJECTS[0]
    public = signed_payload(tmp_path / f"{first}.{VERSION}.nupkg")
    calls = []
    push_packages(tmp_path, VERSION, "fixture", lambda c: (calls.append(c), SimpleNamespace(returncode=0))[1],
                  resume=True, fetch=lambda name, version: public if name == first else None)
    assert len(calls) == 2 * len(PROJECTS) - 1
    assert [Path(c[3]).name for c in calls[:len(PROJECTS)-1]] == [f"{n}.{VERSION}.nupkg" for n in PROJECTS[1:]]
    assert all("--skip-duplicate" not in c for c in calls[:len(PROJECTS)-1])
    assert [Path(c[3]).name for c in calls[len(PROJECTS)-1:]] == [f"{n}.{VERSION}.snupkg" for n in PROJECTS]
    assert all("--skip-duplicate" in c for c in calls[len(PROJECTS)-1:])


@pytest.mark.parametrize("changed", ["dll", "nuspec", "extra"])
def test_resume_conflicts_stop_before_any_upload(tmp_path, changed):
    from publish_packages import push_packages
    artifacts(tmp_path)
    target = tmp_path / f"{PROJECTS[-1]}.{VERSION}.nupkg"
    original = target.read_bytes()
    def change(files):
        key = next(n for n in files if n.endswith('.' + changed)) if changed != "extra" else "unexpected.txt"
        files[key] = b"conflict"
    change_archive(target, change)
    conflict = target.read_bytes()
    target.write_bytes(original)
    calls = []
    with pytest.raises(RuntimeError, match="conflicts"):
        push_packages(tmp_path, VERSION, "fixture", lambda c: calls.append(c), resume=True,
                      fetch=lambda name, version: conflict if name == PROJECTS[-1] else None)
    assert calls == []


def test_resume_symbol_failure_still_stops(tmp_path):
    from types import SimpleNamespace
    from publish_packages import push_packages
    artifacts(tmp_path)
    calls = []
    def upload(command):
        calls.append(command)
        return SimpleNamespace(returncode=1 if command[3].endswith('.snupkg') else 0)
    with pytest.raises(SystemExit):
        push_packages(tmp_path, VERSION, "fixture", upload, resume=True, fetch=lambda *args: None)
    assert len(calls) == len(PROJECTS) + 1


def test_default_does_not_query_public_packages():
    from types import SimpleNamespace
    from publish_packages import push_packages
    def unexpected(*args):
        pytest.fail("Normal publishing must not query or skip existing packages")
    push_packages("packages", VERSION, "fixture", lambda c: SimpleNamespace(returncode=0), fetch=unexpected)


def test_workflow_keeps_default_validation_and_resume_before_login():
    workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/publish-nuget.yml').read_text()
    validate, publish = workflow.split('\n  publish:\n')
    assert 'inputs.artifact-run-id ==' in validate
    assert '--commit "$GITHUB_SHA"' in validate
    assert 'needs.validate.result == \'success\'' in publish
    assert 'needs.validate.result == \'skipped\'' in publish
    assert publish.index('verify_release_run.py') < publish.index('actions/download-artifact@')
    assert publish.index('--preflight-resume') < publish.index('uses: NuGet/login@')
    assert 'environment: nuget' in publish
    assert "github.ref == 'refs/heads/main'" in publish
    assert '--commit "$SOURCE_SHA"' in publish


def test_preflight_validates_bundle_without_credentials(tmp_path, monkeypatch):
    import publish_packages
    artifacts(tmp_path)
    monkeypatch.delenv('NUGET_API_KEY', raising=False)
    monkeypatch.setattr(sys, 'argv', ['publish_packages.py', '--directory', str(tmp_path),
        '--version', VERSION, '--commit', COMMIT, '--preflight-resume'])
    checked = []
    monkeypatch.setattr(publish_packages, 'resume_primaries', lambda output, version: checked.append((output, version)))
    publish_packages.main()
    assert checked == [(tmp_path, VERSION)]
    next(tmp_path.glob('*.snupkg')).unlink()
    with pytest.raises(RuntimeError):
        publish_packages.main()
    assert len(checked) == 1


@pytest.mark.parametrize('status', [403, 429, 500])
def test_public_probe_never_treats_network_failure_as_absent(monkeypatch, status):
    from urllib.error import HTTPError
    import publish_packages
    def failure(*args, **kwargs):
        raise HTTPError('https://fixture', status, 'fixture', {}, None)
    monkeypatch.setattr(publish_packages, 'urlopen', failure)
    with pytest.raises(HTTPError):
        publish_packages.public_package(PROJECTS[0], VERSION)


def test_public_probe_treats_only_404_as_absent(monkeypatch):
    from urllib.error import HTTPError
    import publish_packages
    def missing(*args, **kwargs):
        raise HTTPError('https://fixture', 404, 'fixture', {}, None)
    monkeypatch.setattr(publish_packages, 'urlopen', missing)
    assert publish_packages.public_package(PROJECTS[0], VERSION) is None
