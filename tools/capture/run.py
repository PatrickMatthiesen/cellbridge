"""Start a local diagnostic forward or reverse proxy without changing OS settings."""
from __future__ import annotations

import argparse
import importlib.metadata
import ipaddress
import os
from pathlib import Path
import re
import signal
import sys
import tempfile
from urllib.parse import urlsplit
from datetime import datetime, timezone
from uuid import uuid4

ROOT = Path(__file__).resolve().parent
MITMPROXY_VERSION = "12.2.3"


def positive_int(value: str) -> int:
    number = int(value)
    if number <= 0:
        raise argparse.ArgumentTypeError("must be positive")
    return number


def parse_hosts(value: str) -> list[str]:
    hosts = []
    for item in value.split(","):
        host = item.strip().lower().rstrip(".")
        if not host or any(c in host for c in "/\\@*?%# "):
            raise ValueError("hosts must be exact DNS names or IP addresses, without URLs or wildcards")
        try:
            host = str(ipaddress.ip_address(host))
        except ValueError:
            if not re.fullmatch(r"[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?", host):
                raise ValueError("invalid capture hostname") from None
        if host not in hosts:
            hosts.append(host)
    return hosts


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--hosts", default=os.getenv("CAPTURE_HOSTS"),
                        help="required comma-separated exact destination hostnames")
    parser.add_argument("--reverse-upstream", default=os.getenv("CAPTURE_REVERSE_UPSTREAM"),
                        help="fixed HTTP or HTTPS upstream origin; enables reverse proxy mode")
    parser.add_argument("--tls-cert", type=Path, default=os.getenv("CAPTURE_TLS_CERT"))
    parser.add_argument("--tls-key", type=Path, default=os.getenv("CAPTURE_TLS_KEY"))
    parser.add_argument("--port", type=positive_int, default=int(os.getenv("CAPTURE_PORT", "8877")))
    parser.add_argument("--output-root", type=Path,
                        default=Path(os.getenv("CAPTURE_OUTPUT_ROOT", str(ROOT / "captures"))))
    parser.add_argument("--name", default="session", help="short scenario name; no user or document details")
    parser.add_argument("--max-body-bytes", type=positive_int,
                        default=int(os.getenv("CAPTURE_MAX_BODY_BYTES", str(32 * 1024 * 1024))))
    parser.add_argument("--max-total-bytes", type=positive_int,
                        default=int(os.getenv("CAPTURE_MAX_TOTAL_BYTES", str(512 * 1024 * 1024))))
    parser.add_argument("--max-websocket-message-bytes", type=positive_int,
                        default=int(os.getenv("CAPTURE_MAX_WEBSOCKET_MESSAGE_BYTES", str(32 * 1024 * 1024))))
    parser.add_argument("--max-websocket-messages", type=positive_int,
                        default=int(os.getenv("CAPTURE_MAX_WEBSOCKET_MESSAGES", "10000")))
    parser.add_argument("--upstream-ca", type=Path, default=os.getenv("CAPTURE_UPSTREAM_CA"),
                        help="PEM CA bundle for validating a private upstream certificate")
    args = parser.parse_args()
    if args.reverse_upstream:
        try:
            upstream = urlsplit(args.reverse_upstream)
            if (upstream.scheme not in ("http", "https") or not upstream.hostname or upstream.username
                    or upstream.password or upstream.path not in ("", "/") or upstream.query
                    or upstream.fragment or upstream.port == 0):
                raise ValueError()
            reverse_hosts = parse_hosts(upstream.hostname)
            configured_hosts = parse_hosts(args.hosts) if args.hosts else reverse_hosts
        except ValueError:
            parser.error("reverse upstream must be an HTTP or HTTPS origin without credentials, path, query or fragment")
        if configured_hosts != reverse_hosts:
            parser.error("reverse mode hosts must match the fixed upstream hostname")
        args.hosts = ",".join(reverse_hosts)
        if not args.tls_cert or not args.tls_key or not args.tls_cert.is_file() or not args.tls_key.is_file():
            parser.error("reverse mode requires existing --tls-cert and --tls-key PEM files")
        args.reverse_upstream = args.reverse_upstream.rstrip("/")
        if upstream.scheme == "http":
            args.upstream_ca = None
    elif args.tls_cert or args.tls_key:
        parser.error("TLS certificate options require --reverse-upstream")
    if not args.hosts:
        parser.error("--hosts or CAPTURE_HOSTS is required")
    try:
        hosts = parse_hosts(args.hosts)
    except ValueError as exc:
        parser.error(str(exc))
    if args.port > 65535:
        parser.error("port must be at most 65535")
    if min(args.port, args.max_body_bytes, args.max_total_bytes,
           args.max_websocket_message_bytes, args.max_websocket_messages) <= 0:
        parser.error("port and capture byte limits must be positive")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", args.name):
        parser.error("name must contain 1-64 ASCII letters, digits, underscores or hyphens")
    if args.upstream_ca and not args.upstream_ca.is_file():
        parser.error("upstream CA file does not exist")
    try:
        installed = importlib.metadata.version("mitmproxy")
    except importlib.metadata.PackageNotFoundError:
        parser.error("run setup.ps1 first, then use .venv/Scripts/python.exe")
    if installed != MITMPROXY_VERSION:
        parser.error(f"requires mitmproxy {MITMPROXY_VERSION}; run setup.ps1")

    output_root = args.output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=True)
    run = output_root / f"{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}-{args.name}-{uuid4().hex[:8]}"
    os.environ.update({
        "CAPTURE_OUTPUT": str(run),
        "CAPTURE_HOSTS": ",".join(hosts),
        "CAPTURE_MAX_BODY_BYTES": str(args.max_body_bytes),
        "CAPTURE_MAX_TOTAL_BYTES": str(args.max_total_bytes),
        "CAPTURE_MAX_WEBSOCKET_MESSAGE_BYTES": str(args.max_websocket_message_bytes),
        "CAPTURE_MAX_WEBSOCKET_MESSAGES": str(args.max_websocket_messages),
        "CAPTURE_REVERSE_UPSTREAM": args.reverse_upstream or "",
    })

    from mitmproxy import ctx
    from mitmproxy.tools.main import mitmdump

    # Windows test runners can request a clean shutdown of this process group.
    if hasattr(signal, "SIGBREAK"):
        signal.signal(signal.SIGBREAK, lambda *_: ctx.master.shutdown())

    options = [
        "--mode", f"reverse:{args.reverse_upstream}" if args.reverse_upstream else "regular",
        "--listen-host", "127.0.0.1", "--listen-port", str(args.port),
        "--set", f"confdir={output_root / '.mitmproxy'}",
        "--set", "http2=false", "--set", "connection_strategy=eager",
        "--set", "termlog_verbosity=warn", "--set", "flow_detail=0",
        "--scripts", str(ROOT / "capture_addon.py"),
    ]
    if args.upstream_ca:
        options.extend(["--set", f"ssl_verify_upstream_trusted_ca={args.upstream_ca.resolve()}"])
    print(f"Capture proxy: 127.0.0.1:{args.port}; run: {run}", flush=True)
    print("Local raw evidence only. OS proxy settings and certificate trust are unchanged.", flush=True)
    if args.reverse_upstream:
        # Aspire owns the input files. Keep mitmproxy's combined PEM temporary,
        # outside the evidence directory, and remove it on normal shutdown.
        with tempfile.TemporaryDirectory(prefix="office-capture-tls-") as tls_dir:
            combined = Path(tls_dir) / "server.pem"
            combined.write_bytes(args.tls_cert.read_bytes() + b"\n" + args.tls_key.read_bytes())
            options.extend(["--certs", f"*={combined}", "--set", "upstream_cert=false",
                            "--set", "keep_host_header=true"])
            return mitmdump(options) or 0
    return mitmdump(options) or 0


if __name__ == "__main__":
    sys.exit(main())
