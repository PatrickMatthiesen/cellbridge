"""Check capture and extraction permissions in a separate process."""
import os
from pathlib import Path
import subprocess
import sys

import pytest


@pytest.mark.skipif(os.name == "nt", reason="Unix permissions; Windows requires operator-managed ACLs")
def test_capture_and_extraction_are_private_under_permissive_umask(tmp_path):
    script = r'''
import os
from pathlib import Path
import stat
import sys

root = Path(sys.argv[1])
os.environ["CAPTURE_OUTPUT"] = str(root / "bootstrap")
os.environ["CAPTURE_HOSTS"] = "sharepoint.test"
previous_umask = os.umask(0o022)
try:
    import private_files
    from capture_addon import CaptureAddon
    from inspect_capture import inspect_run
    from test_capture_addon import env, make_flow
    from test_websocket_capture import completed
    import run
    import mitmproxy.tools.main

    original_open = private_files.os.open
    observed_creations = []
    def checked_open(path, flags, mode=0o777, **kwargs):
        descriptor = original_open(path, flags, mode, **kwargs)
        if flags & os.O_CREAT:
            assert stat.S_IMODE(os.fstat(descriptor).st_mode) == 0o600, path
            observed_creations.append(Path(path))
        return descriptor
    private_files.os.open = checked_open

    # Exercise the actual launcher up to entering the transport engine.
    def transport(options):
        # Engine-owned cache files use ordinary file creation, so verify the
        # launcher also applies restrictive creation defaults while it runs.
        key = root / "new" / "output" / ".mitmproxy" / "engine-key.pem"
        key.write_bytes(b"private test key")
        assert stat.S_IMODE(key.stat().st_mode) == 0o600
        return 0
    mitmproxy.tools.main.mitmdump = transport
    sys.argv = ["run.py", "--hosts", "sharepoint.test", "--output-root", str(root / "new" / "output")]
    assert run.main() == 0
    assert os.umask(0o022) == 0o022
    def failed_transport(options):
        raise RuntimeError("simulated transport startup failure")
    mitmproxy.tools.main.mitmdump = failed_transport
    try:
        run.main()
    except RuntimeError:
        pass
    else:
        raise AssertionError("transport startup should fail")
    assert os.umask(0o022) == 0o022

    capture = root / "recordings" / "run"
    completed(capture)
    output = root / "exports" / "nested" / "run"
    assert inspect_run(capture, output) == (4, [])

    pending = root / "pending"
    addon = CaptureAddon(env(pending))
    flow = make_flow()
    addon.requestheaders(flow)
    assert flow.request.stream(b"private payload") == b"private payload"
    parts = list((pending / "files").glob("*.part"))
    assert parts and all(stat.S_IMODE(path.stat().st_mode) == 0o600 for path in parts)
    addon.done()

    # Preserve an existing operator-managed directory, while still creating
    # its children and sensitive files with restrictive permissions.
    existing = root / "existing"
    existing.mkdir(mode=0o755)
    private_files.private_directory(existing)
    assert stat.S_IMODE(existing.stat().st_mode) == 0o755
    private_files.private_write(existing / "new.bin", b"private")

    for path in root.rglob("*"):
        if path == existing:
            continue
        expected = 0o700 if path.is_dir() else 0o600
        assert stat.S_IMODE(path.stat().st_mode) == expected, path
    assert any(path.suffix == ".tmp" for path in observed_creations)
    assert any(path.suffix == ".part" for path in observed_creations)
    assert any("websocket" in str(path) for path in observed_creations)
finally:
    os.umask(previous_umask)
'''
    environment = os.environ.copy()
    environment["PYTHONPATH"] = str(Path(__file__).resolve().parent)
    result = subprocess.run([sys.executable, "-c", script, str(tmp_path)],
                            env=environment, capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stdout + result.stderr
