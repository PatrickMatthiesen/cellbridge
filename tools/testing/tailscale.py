"""Connect the current Aspire web endpoint to a private Tailscale HTTPS listener."""
import argparse
import json
import subprocess
import urllib.parse

from run import APPHOST, ROOT, cli_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--https-port", type=int, default=8444)
    args = parser.parse_args()
    if not 1 <= args.https_port <= 65535:
        parser.error("HTTPS port must be between 1 and 65535.")
    status = cli_json(["tailscale", "status", "--json"])
    if status.get("BackendState") != "Running":
        parser.error("Sign in to Tailscale on this machine first.")
    host = status["Self"]["DNSName"].rstrip(".")
    authority = host if args.https_port == 443 else f"{host}:{args.https_port}"
    origin = "https://" + authority
    subprocess.run(["aspire", "wait", "web", "--apphost", str(APPHOST),
        "--non-interactive"], cwd=ROOT, check=True, timeout=180)
    subprocess.run(["aspire", "wait", "demo", "--apphost", str(APPHOST),
        "--non-interactive"], cwd=ROOT, check=True, timeout=180)
    description = cli_json(["aspire", "describe", "web", "--apphost", str(APPHOST),
        "--format", "Json", "--non-interactive"])
    web = next(r for r in description["resources"] if r["displayName"] == "web")
    if web["environment"].get("Authentication__PublicOrigin", "").rstrip("/") != origin:
        parser.error(f"Start this AppHost with CELLBRIDGE_PUBLIC_ORIGIN={origin} first.")
    endpoint = next(u["url"] for u in web["urls"] if u["name"] == "http" and u.get("isInternal"))
    port = urllib.parse.urlsplit(endpoint).port
    target = f"http://127.0.0.1:{port}"
    # Isolated Aspire uses new ports after restart. Remember the route we own so
    # reconnecting does not overwrite an unrelated service on the same listener.
    state_file = ROOT / "artifacts/tailscale" / f"{args.https_port}.json"
    previous = json.loads(state_file.read_text()) if state_file.exists() else {}
    config = cli_json(["tailscale", "serve", "status", "--json"])
    listener = f"{host}:{args.https_port}"
    handlers = config.get("Web", {}).get(listener, {}).get("Handlers", {})
    tcp = config.get("TCP", {}).get(str(args.https_port), {})
    if config.get("AllowFunnel", {}).get(listener):
        parser.error("This listener has Funnel enabled. Choose a private Serve port.")
    if handlers or tcp:
        allowed = {target}
        if previous.get("origin") == origin:
            allowed.add(previous["target"])
        if not tcp.get("HTTPS") or set(handlers) != {"/"} or handlers["/"].get("Proxy") not in allowed:
            parser.error("This port already serves another application. Choose a free HTTPS port.")
    command = ["tailscale", "serve", "--bg", "--yes", f"--https={args.https_port}", target]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode and "Access denied" in result.stderr:
        result = subprocess.run(["sudo", "-n", *command], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "Tailscale Serve failed.")
    state_file.parent.mkdir(parents=True, exist_ok=True)
    state_file.write_text(json.dumps({"origin": origin, "target": target}, indent=2) + "\n")
    print(f"Library: {origin}/library")
    print(f"Health: {origin}/health")
    print(f"Stop this listener: tailscale serve --https={args.https_port} off")


if __name__ == "__main__":
    main()
