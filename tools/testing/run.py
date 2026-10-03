#!/usr/bin/env python3
"""Run protocol/storage/HTTP checks with an Aspire-owned disposable PostgreSQL."""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import time
import secrets
import http.cookiejar
import html
import re
import ssl
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
APPHOST = ROOT / "aspire/apphost.cs"


def cli_json(command):
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=True)
    # Aspire can prepend a banner to its JSON output.
    for index, character in enumerate(result.stdout):
        if character in "[{":
            try:
                return json.loads(result.stdout[index:])
            except json.JSONDecodeError:
                pass
    raise RuntimeError("Aspire did not return JSON.")


def login(origin, username, password):
    """Use the actual login and CSRF flow; keep cookies out of run reports."""
    if urllib.parse.urlsplit(origin).hostname not in ("localhost", "127.0.0.1"):
        raise RuntimeError("The disposable login helper only accepts loopback origins.")
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar),
        urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
    def page_token():
        with opener.open(origin + "/auth/login") as response:
            page = response.read().decode()
        return html.unescape(re.search(r'name="__RequestVerificationToken" value="([^"]+)"', page)[1])
    token = page_token()
    data = urllib.parse.urlencode({"login": username, "password": password,
        "__RequestVerificationToken": token, "returnUrl": "/auth/complete"}).encode()
    with opener.open(origin + "/auth/login", data) as response:
        if not response.url.endswith("/auth/complete"):
            raise RuntimeError("Disposable account sign-in failed.")
    token = page_token()
    return "; ".join(f"{c.name}={c.value}" for c in jar), token


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--performance", action="store_true", help="Also measure durable synthetic saves/downloads.")
    parser.add_argument("--sizes", default="1,10", help="DOCX sizes in MiB for the performance run.")
    parser.add_argument("--clients", default="1,8")
    parser.add_argument("--iterations", type=int, default=3)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if not shutil.which("aspire") or not shutil.which("dotnet"):
        parser.error("Install Aspire CLI and .NET 10 first.")
    hosts = cli_json(["aspire", "ps", "--format", "Json", "--non-interactive"])
    if any(Path(host["appHostPath"]).resolve() == APPHOST.resolve() for host in hosts):
        parser.error("Stop this workspace's Aspire app before building. Stop and validate any active capture first.")
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    output = (args.output or ROOT / "artifacts/testing" / stamp).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if (output / "summary.json").exists():
        parser.error("Choose a new output directory; this one already contains a run.")
    env = os.environ.copy()
    # Do not inherit external storage or live-test endpoints into an isolated run.
    for key in list(env):
        if key.lower().startswith(("parameters__", "apphost__", "testing__", "cellbridge_")):
            env.pop(key)
    for key in ("ConnectionStrings__cellbridge", "OFFICECOLLABSERVER_INTEROP_ENDPOINT", "OFFICECOLLABSERVER_INTEROP_PEER"):
        env.pop(key, None)
    password = secrets.token_urlsafe(24) + "Aa1!"
    env.update(Testing__Enabled="true", Parameters__TestPassword=password,
               Parameters__WireCaptureDirectory=str(output / "wire"))
    summary = {"startedUtc": stamp, "platform": platform.platform(), "processors": os.cpu_count(),
               "scope": "Protocol replay and synthetic HTTP/storage checks, not desktop Office", "checks": [], "passed": False}
    start_attempted = False

    def run(name, command, timeout=1200):
        print(f"Running {name}", flush=True)
        started = time.monotonic()
        check = {"name": name, "passed": False}
        summary["checks"].append(check)
        try:
            subprocess.run(command, cwd=ROOT, env=env, check=True, timeout=timeout)
            check["passed"] = True
        finally:
            check["seconds"] = round(time.monotonic() - started, 3)

    try:
        run("build", ["dotnet", "build", "CellBridge.slnx", "-c", "Release", "--nologo", "-v", "minimal"])
        run("demo-build", ["dotnet", "build", "demo/CellBridge.Demo.slnx", "-c", "Release", "--nologo", "-v", "minimal"])
        start_attempted = True
        run("aspire-start", ["aspire", "start", "--apphost", str(APPHOST), "--isolated", "--non-interactive"], 600)
        for resource in ("test-account", "web-peer"):
            run(f"start-{resource}", ["aspire", "resource", resource, "start", "--apphost", str(APPHOST),
                "--non-interactive"], 180)
        for resource in ("web", "web-peer", "demo"):
            run(f"ready-{resource}", ["aspire", "wait", resource, "--apphost", str(APPHOST), "--non-interactive"], 180)
        description = cli_json(["aspire", "describe", "--apphost", str(APPHOST), "--non-interactive", "--format", "Json"])
        web = next(r for r in description["resources"] if r.get("displayName") == "web")
        env["ConnectionStrings__cellbridge"] = web["environment"]["ConnectionStrings__cellbridge"]
        peer = next(r for r in description["resources"] if r.get("displayName") == "web-peer")
        web_origin = next(u["url"] for u in web["urls"] if u.get("name") == "https" and u.get("isInternal"))
        peer_origin = next(u["url"] for u in peer["urls"] if u.get("name") == "https" and u.get("isInternal"))
        env["OFFICECOLLABSERVER_INTEROP_ENDPOINT"] = web_origin.rstrip("/") + "/_vti_bin/cellstorage.svc"
        env["OFFICECOLLABSERVER_INTEROP_PEER"] = peer_origin.rstrip("/") + "/_vti_bin/cellstorage.svc"
        env["CELLBRIDGE_INTEROP_COOKIE"], env["CELLBRIDGE_INTEROP_CSRF"] = login(
            web_origin, "integration-writer", password)
        projects = ["CellBridge.FssHttpB.Tests", "CellBridge.FssHttp.Tests", "CellBridge.Storage.Tests",
                    "CellBridge.Interop.Tests", "OfficeInspectors.Adapter"]
        for project in projects:
            run(project, ["dotnet", "test", f"tests/{project}", "-c", "Release", "--no-build",
                "--nologo", "-v", "minimal", "--logger", "trx", "--results-directory", str(output / "tests" / project)])
        run("demo", ["dotnet", "test", "demo/CellBridge.Demo.slnx", "-c", "Release", "--no-build",
            "--nologo", "-v", "minimal", "--logger", "trx", "--results-directory", str(output / "tests")])
        venv = ROOT / "tools/capture/.venv" / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
        run("capture", [str(venv) if venv.exists() else sys.executable, "-m", "pytest", "tools/capture", "-q"])
        run("office-evidence-gates", [str(venv) if venv.exists() else sys.executable, "-m", "pytest", "tools/testing", "-q"])
        if args.performance:
            dll = ROOT / "tools/CellBridge.Storage.Benchmark/bin/Release/net10.0/CellBridge.Storage.Benchmark.dll"
            for backend in ("postgresql", "filesystem"):
                with tempfile.TemporaryDirectory(prefix="cellbridge-perf-") as content_root:
                    run(f"performance-{backend}", ["dotnet", str(dll), "--content", backend, "--root", content_root,
                        "--sizes", args.sizes, "--clients", args.clients, "--iterations", str(args.iterations),
                        "--output", str(output / f"performance-{backend}.json")])
        summary["passed"] = True
    except (subprocess.SubprocessError, RuntimeError, KeyError, StopIteration) as error:
        # Command arguments contain no credentials. Never serialize Aspire's environment.
        summary["passed"] = False
        summary["error"] = str(error)
        print(f"Testing failed: {error}", file=sys.stderr)
    finally:
        if start_attempted:
            try:
                run("aspire-stop", ["aspire", "stop", "--apphost", str(APPHOST), "--non-interactive"], 180)
            except subprocess.SubprocessError:
                summary["passed"] = False
                print("Aspire cleanup failed; inspect aspire ps before the next run.", file=sys.stderr)
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
        print(f"Results: {output}")
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
