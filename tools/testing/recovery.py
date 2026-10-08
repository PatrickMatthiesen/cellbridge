#!/usr/bin/env python3
"""Qualify controlled PostgreSQL interruption and logical restore on disposable Linux resources."""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import platform
import signal
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
APPHOST = ROOT / "tools/testing/recovery/apphost.cs"


def clean_environment():
    env = os.environ.copy()
    for key in list(env):
        if key.lower().startswith(("connectionstrings__", "parameters__", "storage__",
                "apphost__", "testing__", "cellbridge_", "officecollabserver_")):
            del env[key]
    return env


def json_output(command, env):
    result = subprocess.run(command, cwd=APPHOST.parent if command[0] == "aspire" else ROOT, env=env, capture_output=True,
                            text=True, check=True, timeout=120)
    for index, character in enumerate(result.stdout):
        if character in "[{":
            try:
                return json.loads(result.stdout[index:])
            except json.JSONDecodeError:
                pass
    raise RuntimeError("Command returned no JSON.")


def owned_container(container, marker):
    """Do not return raw inspect data: it contains the generated password."""
    config = container["Config"]
    if (config["Labels"].get("cellbridge.recovery") != marker
            or config["Image"] not in ("docker.io/library/postgres:18.3", "postgres:18.3")
            or f"CELLBRIDGE_RECOVERY_RUN={marker}" not in config["Env"]
            or f"PGDATA=/cellbridge-recovery-data/{marker}" not in config["Env"]
            or container["HostConfig"].get("Tmpfs") != {"/var/lib/postgresql": "rw"}
            or any(m["Type"] != "tmpfs" or m["Destination"] != "/var/lib/postgresql"
                   for m in container["Mounts"])):
        raise RuntimeError("Recovery container ownership or disposable storage check failed.")
    return container["Id"]


def interrupted(signum, frame):
    raise KeyboardInterrupt(f"Interrupted by signal {signum}.")


def run_process(command, env, log, timeout):
    with subprocess.Popen(command, cwd=APPHOST.parent if command[0] == "aspire" else ROOT,
                          env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True) as process:
        try:
            if process.wait(timeout=timeout):
                raise subprocess.CalledProcessError(process.returncode, command)
        except BaseException:
            # Reap this command and terminate its descendants on timeout, Ctrl-C
            # or SIGTERM before resource cleanup. The process group is ours.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            raise


def logged_process(command, env, path, timeout, cleanup=False):
    try:
        log = path.open("w")
    except OSError as error:
        if not cleanup:
            raise
        # Disk/log failure must not prevent stopping or disposing our resources.
        message = f"Could not write {path.name}: {error}"
        try:
            run_process(command, env, subprocess.DEVNULL, timeout)
        except (subprocess.SubprocessError, OSError) as command_error:
            raise RuntimeError(f"{message}; cleanup command failed: {command_error}") from command_error
        return message
    with log:
        run_process(command, env, log, timeout)
    return None


def cleanup_resources(marker, env, run):
    errors = []
    try:
        log_error = run("aspire-stop", ["aspire", "stop", "--apphost", str(APPHOST), "--non-interactive"], 180)
        if log_error:
            errors.append(log_error)
    except (subprocess.SubprocessError, RuntimeError, OSError) as error:
        errors.append(str(error))
    # Disposal remains independent of Aspire shutdown success. Inspect ownership
    # again, including containers created during a partially completed startup.
    try:
        ids = subprocess.check_output(["docker", "ps", "-aq", "--filter", f"label=cellbridge.recovery={marker}"],
                                      env=env, text=True, timeout=30).split()
    except (subprocess.SubprocessError, OSError) as error:
        errors.append(str(error))
        return errors
    for candidate in ids:
        try:
            owned = owned_container(json_output(["docker", "inspect", candidate], env)[0], marker)
            log_error = run("dispose-" + owned[:12], ["docker", "rm", "--force", owned], 60)
            if log_error:
                errors.append(log_error)
        except (subprocess.SubprocessError, RuntimeError, KeyError, OSError) as error:
            errors.append(str(error))
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if sys.platform != "linux":
        parser.error("This controlled database runner requires Linux.")
    output = (args.output or ROOT / "artifacts/recovery" /
              dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")).resolve()
    # Raw backups and test state must stay under the repository's ignored artifacts.
    if not output.is_relative_to(ROOT / "artifacts"):
        parser.error("Output must be under this worktree's ignored artifacts directory.")
    output.mkdir(parents=True, exist_ok=False)
    env = clean_environment()
    marker = uuid.uuid4().hex
    env.update(CELLBRIDGE_RECOVERY_RUN=marker, CELLBRIDGE_RECOVERY_OUTPUT=str(output),
               CELLBRIDGE_RECOVERY_APPHOST=str(APPHOST))
    summary = {"sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "sourceDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()),
               "platform": platform.platform(), "hostOsRelease": Path("/etc/os-release").read_text(),
               "scope": "Controlled PostgreSQL process interruption and quiescent logical backup; no failover, hardware power-loss or desktop qualification",
               "checks": [], "passed": False}
    attempted = False
    container_id = None
    previous_signals = {sig: signal.signal(sig, interrupted) for sig in (signal.SIGINT, signal.SIGTERM)}

    def run(name, command, timeout=600):
        print(f"Running {name}", flush=True)
        check = {"name": name, "passed": False}
        summary["checks"].append(check)
        log_error = logged_process(command, env, output / (name + ".log"), timeout,
                                   cleanup=name == "aspire-stop" or name.startswith("dispose-"))
        check["passed"] = True
        if log_error:
            check["logError"] = log_error
        return log_error

    try:
        hosts = json_output(["aspire", "ps", "--format", "Json", "--non-interactive"], env)
        if any(Path(h["appHostPath"]).resolve().is_relative_to(ROOT) for h in hosts):
            raise RuntimeError("This worktree has an active AppHost; stop its capture/app before building.")
        for name, command in (("dotnetSdk", ["dotnet", "--version"]), ("aspireCli", ["aspire", "--version"]),
                              ("dockerServer", ["docker", "version", "--format", "{{.Server.Version}}"] )):
            summary[name] = subprocess.check_output(command, cwd=ROOT, env=env, text=True).strip()
        run("build", ["dotnet", "build", "tests/CellBridge.Storage.Tests", "-c", "Release", "--nologo", "-v", "minimal"])
        run("setup-build", ["dotnet", "build", "tools/CellBridge.Storage.Setup", "-c", "Release", "--nologo", "-v", "minimal"])
        attempted = True
        run("aspire-start", ["aspire", "start", "--apphost", str(APPHOST), "--isolated", "--non-interactive"])
        run("aspire-ready", ["aspire", "wait", "recovery-storage", "--apphost", str(APPHOST), "--non-interactive"], 180)
        run("connection-ready", ["aspire", "wait", "connection", "--apphost", str(APPHOST), "--non-interactive"], 180)
        ids = subprocess.check_output(["docker", "ps", "-aq", "--filter", f"label=cellbridge.recovery={marker}"], env=env, text=True).split()
        if len(ids) != 1:
            raise RuntimeError("Expected exactly one container for this run.")
        container = json_output(["docker", "inspect", ids[0]], env)[0]
        container_id = owned_container(container, marker)
        description = json_output(["aspire", "describe", "--apphost", str(APPHOST), "--format", "Json", "--non-interactive"], env)
        connection = next(r for r in description["resources"] if r.get("displayName") == "connection")
        env["ConnectionStrings__cellbridge"] = connection["environment"]["ConnectionStrings__recovery-storage"]
        env["CELLBRIDGE_RECOVERY_CONTAINER"] = container_id
        summary.update(containerId=container_id, imageId=container["Image"], mounts=container["Mounts"],
                       tmpfs=container["HostConfig"].get("Tmpfs"))
        summary["imageDigests"] = json_output(["docker", "image", "inspect", container["Image"]], env)[0]["RepoDigests"]
        run("database-recovery", ["dotnet", "test", "tests/CellBridge.Storage.Tests", "-c", "Release", "--no-build",
            "--nologo", "-v", "normal", "--filter", "FullyQualifiedName~DatabaseRecoveryTests",
            "--logger", "trx", "--results-directory", str(output / "tests")], 900)
        results = [result for path in (output / "tests").glob("*.trx")
                   for result in ET.parse(path).getroot().iter()
                   if result.tag.endswith("}UnitTestResult")]
        if len(results) != 6 or any(result.get("outcome") != "Passed" for result in results):
            raise RuntimeError("All six database recovery cases must execute and pass; skips are not qualification.")
        env["CELLBRIDGE_QUALIFICATION_DIRECTORY"] = str(output / "environment")
        run("initialize-test-database", ["dotnet", str(ROOT / "tools/CellBridge.Storage.Setup/bin/Release/net10.0/CellBridge.Storage.Setup.dll")])
        run("existing-recovery", ["dotnet", "test", "tests/CellBridge.Storage.Tests", "-c", "Release", "--no-build",
            "--nologo", "-v", "minimal", "--filter",
            "FullyQualifiedName~ProcessTests|FullyQualifiedName~PortabilityProviderTests|FullyQualifiedName~QualificationEnvironmentTests",
            "--logger", "trx", "--results-directory", str(output / "existing-tests")], 900)
        summary["passed"] = True
    except (subprocess.SubprocessError, RuntimeError, KeyError, StopIteration, KeyboardInterrupt, OSError) as error:
        summary["error"] = str(error)
        print(f"Recovery qualification failed: {error}", file=sys.stderr)
    finally:
        for sig in previous_signals:
            signal.signal(sig, signal.SIG_IGN)
        if attempted:
            errors = cleanup_resources(marker, env, run)
            if errors:
                summary["passed"] = False
                summary["cleanupErrors"] = errors
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
        print(f"Results: {output}")
        for sig, handler in previous_signals.items():
            signal.signal(sig, handler)
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
