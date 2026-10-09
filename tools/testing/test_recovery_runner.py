"""The destructive runner must reject development state and accept only owned containers."""
import copy
import importlib.util
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

import pytest

spec = importlib.util.spec_from_file_location("database_recovery_runner", Path(__file__).with_name("recovery.py"))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def test_inherited_connection_and_storage_configuration_is_removed(monkeypatch):
    for name in ("ConnectionStrings__cellbridge", "CONNECTIONSTRINGS__other", "Storage__ContentRoot",
                 "Parameters__StorageVolume", "CELLBRIDGE_RECOVERY_CONTAINER", "Testing__Enabled"):
        monkeypatch.setenv(name, "external-development-state")
    env = runner.clean_environment()
    assert all(value != "external-development-state" for value in env.values())
    assert env["PATH"] == os.environ["PATH"]


def container():
    marker = "a" * 32
    return marker, {"Id": "b" * 64, "Config": {"Image": "postgres:18.3",
        "Labels": {"cellbridge.recovery": marker}, "Env": [f"CELLBRIDGE_RECOVERY_RUN={marker}",
        f"PGDATA=/cellbridge-recovery-data/{marker}"]}, "HostConfig": {"Tmpfs": {"/var/lib/postgresql": "rw"}},
        "Mounts": [{"Type": "tmpfs", "Destination": "/var/lib/postgresql"}]}


@pytest.mark.parametrize("defect", ["marker", "image", "pgdata", "bind", "volume", "data-mount", "hidden-pgdata-tmpfs"])
def test_refuses_foreign_or_persistent_container(defect):
    marker, value = container()
    value = copy.deepcopy(value)
    if defect == "marker":
        value["Config"]["Labels"]["cellbridge.recovery"] = "foreign"
    elif defect == "image":
        value["Config"]["Image"] = "postgres:17"
    elif defect == "pgdata":
        value["Config"]["Env"][1] = "PGDATA=/var/lib/postgresql/data"
    elif defect in ("bind", "volume"):
        value["Mounts"][0]["Type"] = defect
    elif defect == "data-mount":
        value["Mounts"][0]["Destination"] = "/cellbridge-recovery-data"
    else:
        value["HostConfig"]["Tmpfs"]["/cellbridge-recovery-data"] = "rw"
    with pytest.raises(RuntimeError, match="ownership or disposable storage"):
        runner.owned_container(value, marker)


def test_accepts_owned_container_with_only_unused_image_tmpfs():
    marker, value = container()
    assert runner.owned_container(value, marker) == value["Id"]


def test_container_disposal_runs_after_failed_aspire_stop(monkeypatch):
    marker, value = container()
    monkeypatch.setattr(runner.subprocess, "check_output", lambda *args, **kwargs: value["Id"])
    monkeypatch.setattr(runner, "json_output", lambda *args: [value])
    calls = []

    def run(name, command, timeout):
        calls.append(command)
        if name == "aspire-stop":
            raise subprocess.CalledProcessError(1, command)

    errors = runner.cleanup_resources(marker, {}, run)
    assert len(errors) == 1
    assert calls[-1] == ["docker", "rm", "--force", value["Id"]]


def test_cleanup_retains_shutdown_and_disposal_errors(monkeypatch):
    marker, value = container()
    monkeypatch.setattr(runner.subprocess, "check_output", lambda *args, **kwargs: value["Id"])
    monkeypatch.setattr(runner, "json_output", lambda *args: [value])

    def run(name, command, timeout):
        raise subprocess.CalledProcessError(1, command)

    errors = runner.cleanup_resources(marker, {}, run)
    assert len(errors) == 2
    assert "aspire" in errors[0] and "docker" in errors[1]


def test_disk_full_logs_do_not_prevent_shutdown_or_disposal(monkeypatch, tmp_path):
    marker, value = container()
    monkeypatch.setattr(runner.subprocess, "check_output", lambda *args, **kwargs: value["Id"])
    monkeypatch.setattr(runner, "json_output", lambda *args: [value])
    calls = []

    def cannot_open(*args, **kwargs):
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(Path, "open", cannot_open)
    monkeypatch.setattr(runner, "run_process", lambda command, env, log, timeout: calls.append((command, log)))

    def run(name, command, timeout):
        return runner.logged_process(command, {}, tmp_path / (name + ".log"), timeout, cleanup=True)

    errors = runner.cleanup_resources(marker, {}, run)
    assert len(errors) == 2 and all("No space left on device" in error for error in errors)
    assert calls[0][0][0:2] == ["aspire", "stop"]
    assert calls[1][0] == ["docker", "rm", "--force", value["Id"]]
    assert all(log == subprocess.DEVNULL for _, log in calls)


@pytest.mark.skipif(sys.platform != "linux", reason="Recovery runner process groups require Linux.")
@pytest.mark.parametrize("signum", [signal.SIGINT, signal.SIGTERM])
def test_interruption_reaps_active_command_and_stops_its_descendants(tmp_path, signum):
    pid_file = tmp_path / "pids"
    child_code = (
        "import os,subprocess,sys,time; from pathlib import Path; "
        "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); "
        f"Path({str(pid_file)!r}).write_text(str(os.getpid())+' '+str(child.pid)); time.sleep(60)"
    )
    driver_code = (
        "import importlib.util,os,signal,sys; "
        f"s=importlib.util.spec_from_file_location('r',{str(Path(runner.__file__).resolve())!r}); "
        "r=importlib.util.module_from_spec(s); s.loader.exec_module(r); "
        "signal.signal(signal.SIGINT,r.interrupted); signal.signal(signal.SIGTERM,r.interrupted)\n"
        "try:\n"
        f" with open({str(tmp_path / 'command.log')!r},'w') as log: "
        f"r.run_process([sys.executable,'-c',{child_code!r}],os.environ.copy(),log,30)\n"
        "except KeyboardInterrupt: sys.exit(0)\n"
    )
    driver = subprocess.Popen([sys.executable, "-c", driver_code], start_new_session=True)
    try:
        deadline = time.monotonic() + 10
        while not pid_file.exists() and time.monotonic() < deadline:
            time.sleep(0.025)
        assert pid_file.exists(), "Test command did not start."
        command_pid, descendant_pid = map(int, pid_file.read_text().split())
        driver.send_signal(signum)
        assert driver.wait(timeout=10) == 0
        # The direct child is reaped. An orphaned grandchild may await the host's
        # reaper as a zombie, but must have stopped executing before cleanup.
        assert not Path(f"/proc/{command_pid}").exists()
        stat = Path(f"/proc/{descendant_pid}/stat")
        assert not stat.exists() or stat.read_text().split()[2] == "Z"
    finally:
        if driver.poll() is None:
            driver.kill()
            driver.wait()
