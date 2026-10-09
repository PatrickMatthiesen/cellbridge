"""Regressions for fixture shutdown without signalling the invoking console."""
from types import SimpleNamespace
from unittest.mock import Mock
import io
import json
import subprocess

from tools.capture import test_proxy_integration as integration


def case_with_process(tmp_path, process):
    case = integration.ProxyIntegrationTests("runTest")
    case.output_root = tmp_path
    case.proc = process
    case.proxy_log = io.BytesIO()
    return case


def test_shutdown_acknowledgement_waits_for_process_exit(tmp_path, monkeypatch):
    run = tmp_path / "run"
    run.mkdir()
    (run / "manifest.json").write_text(json.dumps({"status": "complete"}))
    process = Mock(stdout=io.BytesIO())
    process.poll.return_value = None
    process.wait.return_value = 0
    process.send_signal.side_effect = AssertionError("console control events must never be sent")
    process.terminate.side_effect = AssertionError("allow the acknowledged shutdown to finish")
    case = case_with_process(tmp_path, process)
    monkeypatch.setattr(integration, "os", SimpleNamespace(name="nt"))
    monkeypatch.setattr(integration, "signal", SimpleNamespace(CTRL_BREAK_EVENT=1), raising=False)
    monkeypatch.setattr(integration.subprocess, "run", Mock(return_value=SimpleNamespace(returncode=0)))
    case._stop_proxy()
    process.wait.assert_called_once_with(timeout=10)
    process.send_signal.assert_not_called()
    process.terminate.assert_not_called()
    process.kill.assert_not_called()


def test_unresponsive_proxy_uses_targeted_terminate_then_kill(tmp_path):
    process = Mock(stdout=io.BytesIO())
    process.poll.return_value = None
    process.wait.side_effect = [subprocess.TimeoutExpired("proxy", 10),
                                subprocess.TimeoutExpired("proxy", 5), 0]
    case = case_with_process(tmp_path, process)
    case._stop_proxy()
    assert [call[0] for call in process.method_calls] == ["poll", "wait", "terminate", "wait", "kill", "wait"]
    process.send_signal.assert_not_called()
    assert case.proxy_log.closed


def test_every_proxy_launch_uses_windows_isolation_and_a_file(tmp_path, monkeypatch):
    case = integration.ProxyIntegrationTests("runTest")
    case.temp = SimpleNamespace(name=str(tmp_path))
    process = Mock()
    process.poll.return_value = 0
    launch = Mock(return_value=process)
    monkeypatch.setattr(integration, "os", SimpleNamespace(name="nt"))
    monkeypatch.setattr(integration.subprocess, "CREATE_NO_WINDOW", 0x08000000, raising=False)
    monkeypatch.setattr(integration.subprocess, "Popen", launch)
    case._launch_proxy(["synthetic-proxy"])
    try:
        options = launch.call_args.kwargs
        assert options["creationflags"] == 0x08000000
        assert options["stdin"] == subprocess.DEVNULL
        assert options["stdout"] is case.proxy_log
        assert options["stdout"] != subprocess.PIPE
        assert options["stdout"].fileno() >= 0
    finally:
        case._stop_proxy()


def test_large_child_diagnostics_do_not_block_graceful_stop(tmp_path):
    import os
    import sys
    import time

    case = integration.ProxyIntegrationTests("runTest")
    case.temp = SimpleNamespace(name=str(tmp_path))
    case.output_root = tmp_path / "output"
    run = case.output_root / "run"
    # Write much more than a Windows anonymous pipe can hold, then participate
    # in the same file-based stop handshake as the proxy. No console signals.
    script = '''
import json
from pathlib import Path
import sys
import time
root = Path(sys.argv[1])
root.mkdir(parents=True)
sys.stdout.buffer.write(b"x" * (1024 * 1024))
sys.stdout.buffer.flush()
def state(status):
    temp = root / "manifest.tmp"
    temp.write_text(json.dumps({"status": status}))
    temp.replace(root / "manifest.json")
state("running")
while not (root / "stop.request").exists():
    time.sleep(0.01)
state("complete")
'''
    case._launch_proxy([sys.executable, "-c", script, str(run)])
    try:
        deadline = time.monotonic() + 15
        while not (run / "manifest.json").exists() and time.monotonic() < deadline:
            assert case.proc.poll() is None
            time.sleep(0.05)
        assert (run / "manifest.json").exists(), "child output blocked before shutdown was ready"
        assert os.fstat(case.proxy_log.fileno()).st_size >= 1024 * 1024
        case._stop_proxy()
        assert case.proc.returncode == 0
        assert json.loads((run / "manifest.json").read_text())["status"] == "complete"
        assert case.proxy_log.closed
    finally:
        case._stop_proxy()
