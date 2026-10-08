"""Black-box tests for the real mitmproxy capture launcher.

Run with ``python -m unittest tools.capture.test_proxy_integration`` (or the
capture kit's pinned virtual-environment interpreter). These tests start an
in-process upstream and the production proxy subprocess. They never alter the
machine proxy settings or certificate store.
"""

from __future__ import annotations

import gzip
from datetime import datetime, timedelta, timezone
import http.client
import json
import os
import socket
import ssl
import subprocess
import signal
import sys
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import ClassVar

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID

from tools.capture import websocket_test_peer


ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "tools" / "capture" / "run.py"
VENV_PYTHON = ROOT / "tools" / "capture" / ".venv" / "Scripts" / "python.exe"
PROXY_PYTHON = str(VENV_PYTHON) if VENV_PYTHON.exists() else sys.executable


class FakeUpstreamHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    received: ClassVar[list[tuple[str, bytes, str | None]]] = []
    lock: ClassVar[threading.Lock] = threading.Lock()

    def log_message(self, *_args: object) -> None:
        pass

    def handle(self) -> None:
        try:
            super().handle()
        except (ConnectionResetError, BrokenPipeError, ssl.SSLError):
            # A client can close an idle keep-alive socket while the test ends.
            pass

    def _read_body(self) -> bytes:
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            chunks = []
            while True:
                size_line = self.rfile.readline().split(b";", 1)[0].strip()
                size = int(size_line, 16)
                if size == 0:
                    while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                        pass
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
            return b"".join(chunks)
        return self.rfile.read(int(self.headers.get("Content-Length", "0")))

    def _handle(self) -> None:
        if self.headers.get("Upgrade", "").lower() == "websocket" and not self.path.endswith("/ws-rejected"):
            websocket_test_peer.serve(self)
            return
        body = self._read_body()
        with self.lock:
            self.received.append((self.command, body, self.headers.get("Authorization")))
        if self.path.endswith("/redirect"):
            self.send_response(307)
            self.send_header("Location", "/destination")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if self.path.endswith("/head"):
            self.send_response(200)
            self.send_header("X-Upstream", "head-ok")
            self.send_header("Content-Length", "11")
            self.end_headers()
            return
        if self.path.endswith("/gzip"):
            response = gzip.compress(b"compressed response bytes")
            self.send_response(200)
            self.send_header("Content-Encoding", "gzip")
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)
            return
        if self.path.endswith("/challenge"):
            response = b"challenge"
            self.send_response(401)
            self.send_header("WWW-Authenticate", 'NTLM TlRMTVNTUAACAAAA')
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)
            return
        response = b"upstream:" + body
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(response)))
        self.end_headers()
        self.wfile.write(response)

    do_GET = _handle
    do_POST = _handle
    do_PUT = _handle
    do_HEAD = _handle


class ProxyIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.upstream = ThreadingHTTPServer(("127.0.0.1", 0), FakeUpstreamHandler)
        cls.upstream_thread = threading.Thread(target=cls.upstream.serve_forever, daemon=True)
        cls.upstream_thread.start()
        cls.upstream_port = cls.upstream.server_port

    @classmethod
    def tearDownClass(cls) -> None:
        cls.upstream.shutdown()
        cls.upstream.server_close()
        cls.upstream_thread.join(timeout=3)

    def setUp(self) -> None:
        self.assertTrue(RUNNER.exists(), "capture launcher tools/capture/run.py is required")
        with FakeUpstreamHandler.lock:
            FakeUpstreamHandler.received.clear()
        self.temp = tempfile.TemporaryDirectory(prefix="office-capture-test-")
        self.addCleanup(self.temp.cleanup)
        self.output_root = Path(self.temp.name) / "captures"
        self.port = self._free_port()
        args = [
            PROXY_PYTHON, str(RUNNER), "--hosts", "localhost,127.0.0.1",
            "--port", str(self.port), "--output-root", str(self.output_root),
            "--name", "integration", "--max-body-bytes", "1048576",
            "--max-total-bytes", "4194304",
        ]
        creationflags = ((getattr(subprocess, "CREATE_NO_WINDOW", 0) |
                          getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)) if os.name == "nt" else 0)
        self.proc = subprocess.Popen(
            args, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, creationflags=creationflags,
        )
        self.addCleanup(self._stop_proxy)
        self._wait_ready()

    @staticmethod
    def _free_port() -> int:
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            return int(sock.getsockname()[1])

    def _wait_ready(self) -> None:
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                output = self.proc.stdout.read().decode("utf-8", "replace") if self.proc.stdout else ""
                self.fail(f"capture proxy exited during startup ({self.proc.returncode}):\n{output}")
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.2):
                    return
            except OSError:
                time.sleep(0.1)
        self.fail("capture proxy did not listen within 15 seconds")

    def _stop_proxy(self) -> None:
        if self.proc.poll() is None:
            run_dirs = list(self.output_root.glob("*/manifest.json"))
            if run_dirs:
                try:
                    subprocess.run([sys.executable, str(ROOT / "tools" / "capture" / "stop.py"),
                                    str(run_dirs[0].parent)], cwd=ROOT, timeout=12,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
                except subprocess.TimeoutExpired:
                    pass
            if self.proc.poll() is None and os.name == "nt" and hasattr(signal, "CTRL_BREAK_EVENT"):
                self.proc.send_signal(signal.CTRL_BREAK_EVENT)
            elif self.proc.poll() is None:
                self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)
        if self.proc.stdout is not None:
            self.proc.stdout.close()

    def _validate_run(self) -> None:
        from tools.capture.inspect_capture import validate_run

        run_dirs = list(self.output_root.glob("*/manifest.json"))
        self.assertEqual(len(run_dirs), 1, f"expected one capture run under {self.output_root}")
        _, problems = validate_run(run_dirs[0].parent)
        self.assertEqual(problems, [])

    def _request(self, method: str, path: str, body: bytes = b"", headers: dict[str, str] | None = None,
                 *, chunked: bool = False) -> tuple[int, dict[str, str], bytes]:
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        self.addCleanup(conn.close)
        target = f"http://localhost:{self.upstream_port}{path}"
        conn.request(method, target, body=[body[:3], body[3:]] if chunked else body,
                     headers=headers or {}, encode_chunked=chunked)
        response = conn.getresponse()
        return response.status, dict(response.getheaders()), response.read()

    def _connect_tls(self, upstream_port: int, context: ssl.SSLContext) -> ssl.SSLSocket:
        sock = socket.create_connection(("127.0.0.1", self.port), timeout=5)
        sock.sendall(f"CONNECT localhost:{upstream_port} HTTP/1.1\r\nHost: localhost:{upstream_port}\r\n\r\n".encode())
        reader = sock.makefile("rb")
        status = reader.readline()
        while reader.readline() not in (b"\r\n", b"\n", b""):
            pass
        self.assertIn(b" 200 ", status, f"CONNECT failed: {status!r}")
        return context.wrap_socket(sock, server_hostname="localhost")

    def _flow_records(self) -> list[dict]:
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            run_dir = self._run_dir()
            records = [json.loads(p.read_text("utf-8")) for p in (run_dir / "flows").glob("*.json")]
            if records and all(record.get("completeness") != "pending" for record in records):
                return sorted(records, key=lambda record: record["sequence"])
            time.sleep(0.05)
        return sorted((json.loads(p.read_text("utf-8")) for p in (self._run_dir() / "flows").glob("*.json")),
                      key=lambda record: record["sequence"])

    def _run_dir(self) -> Path:
        runs = list(self.output_root.glob("*/manifest.json"))
        return runs[0].parent if runs else self.output_root

    def _websocket_records(self):
        records = self._flow_records()
        return [r for r in records if "websocket" in r]

    def test_websocket_messages_fragmentation_order_and_http_fallback(self):
        # Negotiation and fallback remain ordinary HTTP; no SignalR semantics
        # are inferred from these synthetic paths and payloads.
        self.assertEqual(self._request("POST", "/negotiate", b"synthetic")[0], 200)
        a = websocket_test_peer.Client(self.port, self.upstream_port)
        b = websocket_test_peer.Client(self.port, self.upstream_port)
        self.addCleanup(a.close)
        self.addCleanup(b.close)
        text = "local \u00f8 \U0001f642".encode()
        a.send(1, text[:7], final=False)
        a.send(0, text[7:])
        self.assertEqual(a.receive(), (1, text))
        binary = bytes(range(256)) * 300  # 64-bit frame length, arbitrary bytes.
        b.send(2, binary)
        self.assertEqual(b.receive(), (2, binary))
        a.send(2, b"")
        self.assertEqual(a.receive(), (2, b""))
        a.send(9, b"ping")
        self.assertEqual(a.receive(), (10, b"ping"))
        a.finish()
        b.finish(1001)
        self.assertEqual(self._request("GET", "/ws-rejected", headers={"Upgrade": "websocket",
            "Connection": "Upgrade", "Sec-WebSocket-Version": "13"})[0], 200)
        self.assertEqual(self._request("GET", "/fallback")[2], b"upstream:")
        self._stop_proxy()
        self._validate_run()
        records = self._websocket_records()
        self.assertEqual(len(records), 2)
        self.assertNotEqual(records[0]["request"]["client_connection_id"], records[1]["request"]["client_connection_id"])
        self.assertNotEqual(records[0]["response"]["server_connection_id"], records[1]["response"]["server_connection_id"])
        run = self._run_dir()
        payloads = []
        for record in records:
            self.assertEqual(record["response"]["status_code"], 101)
            paths = sorted((run / "websockets" / record["flow_id"]).glob("*.json"))
            messages = [json.loads(p.read_text()) for p in paths]
            self.assertEqual([m["sequence"] for m in messages], list(range(1, len(messages) + 1)))
            self.assertEqual([m["direction"] for m in messages], ["client_to_server", "server_to_client"] * (len(messages) // 2))
            payloads.extend((run / m["payload"]["file"]).read_bytes() for m in messages)
        self.assertCountEqual(payloads, [text, text, binary, binary, b"", b""])
        self.assertTrue(any(r["request"]["path"] == "/negotiate" for r in self._flow_records()))
        self.assertTrue(any(r["request"]["path"] == "/fallback" for r in self._flow_records()))

    def test_websocket_abrupt_disconnect_and_protocol_error_are_invalid(self):
        for path in ("/ws-abort", "/ws-error"):
            client = websocket_test_peer.Client(self.port, self.upstream_port, path)
            self.addCleanup(client.close)
            opcode, payload = client.receive()
            self.assertEqual(opcode, 8)
            # 1006 is a local abnormal-close state and cannot be sent on wire.
            self.assertIn(int.from_bytes(payload[:2], "big"), (1000, 1002))
            client.close()
        self._stop_proxy()
        from tools.capture.inspect_capture import validate_run
        self.assertTrue(validate_run(self._run_dir())[1])
        records = self._websocket_records()
        self.assertEqual(len(records), 2)
        self.assertTrue(all(r["websocket"]["end"]["close_code"] in (1002, 1006) for r in records))
        self.assertTrue(all(r["websocket"]["recorded_messages"] == 0 for r in records))

    def test_websocket_normal_close_after_partial_fragment_exposes_hook_limit(self):
        client = websocket_test_peer.Client(self.port, self.upstream_port)
        self.addCleanup(client.close)
        client.send(1, b"unfinished message", final=False)
        client.finish()
        self._stop_proxy()
        self._validate_run()
        # The pinned engine emits no message hook for this partial message.
        # Completion is explicitly scoped to completed application messages.
        record = self._websocket_records()[0]["websocket"]
        self.assertEqual(record["observed_messages"], 0)
        manifest = json.loads((self._run_dir() / "manifest.json").read_text())
        self.assertEqual(manifest["websocket_capture_scope"], "completed_application_messages")

    def test_websocket_limits_preserve_forwarding_and_mark_run_invalid(self):
        self._stop_proxy()
        self.output_root = Path(self.temp.name) / "websocket-limited"
        args = [PROXY_PYTHON, str(RUNNER), "--hosts", "localhost", "--port", str(self.port),
                "--output-root", str(self.output_root), "--max-websocket-message-bytes", "3",
                "--max-websocket-messages", "2", "--max-total-bytes", "5"]
        self.proc = subprocess.Popen(args, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                     stderr=subprocess.STDOUT)
        self._wait_ready()
        client = websocket_test_peer.Client(self.port, self.upstream_port)
        self.addCleanup(client.close)
        for payload in (b"unchanged", b"still forwarded", b"after limits"):
            client.send(2, payload)
            self.assertEqual(client.receive(), (2, payload))
        client.finish()
        self._stop_proxy()
        from tools.capture.inspect_capture import validate_run
        self.assertTrue(validate_run(self._run_dir())[1])
        record = self._websocket_records()[0]["websocket"]
        self.assertEqual(record["observed_messages"], 6)
        self.assertEqual(record["recorded_messages"], 2)
        self.assertEqual(record["dropped_messages"], 4)
        self.assertEqual(record["captured_bytes"], 5)

    def test_binary_multipart_upload_download_and_auth_redaction(self) -> None:
        boundary = b"--fsshttp-test-boundary\r\n"
        upload = boundary + b"Content-Type: application/octet-stream\r\n\r\n\x00\xff\x80binary\r\n--fsshttp-test-boundary--\r\n"
        status, _, response = self._request("POST", "/cellstorage.svc", upload, {
            "Content-Type": "multipart/related; boundary=fsshttp-test-boundary",
            "Authorization": "Negotiate secret-token",
        })
        self.assertEqual(status, 200)
        self.assertEqual(response, b"upstream:" + upload)
        with FakeUpstreamHandler.lock:
            self.assertIn(("POST", upload, "Negotiate secret-token"), FakeUpstreamHandler.received)
        records = self._flow_records()
        matching = [r for r in records if r["request"]["path"].endswith("cellstorage.svc")]
        self.assertTrue(matching, f"expected cellstorage flow; captured records: {records!r}")
        record = matching[0]
        req_meta = record["request_body"]
        resp_meta = record["response_body"]
        run_dir = self._run_dir()
        self.assertEqual((run_dir / req_meta["file"]).read_bytes(), upload)
        self.assertEqual((run_dir / resp_meta["file"]).read_bytes(), b"upstream:" + upload)
        serialized = json.dumps(record)
        self.assertNotIn("secret-token", serialized)
        self.assertIn("[REDACTED]", serialized)

    def test_gzip_chunked_head_and_redirect_are_forwarded(self) -> None:
        status, headers, body = self._request("GET", "/gzip")
        self.assertEqual(status, 200)
        self.assertEqual(headers["Content-Encoding"], "gzip")
        self.assertEqual(gzip.decompress(body), b"compressed response bytes")
        status, _, body = self._request("PUT", "/chunked", b"chunked-\x00-payload", chunked=True)
        self.assertEqual((status, body), (200, b"upstream:chunked-\x00-payload"))
        status, headers, body = self._request("HEAD", "/head")
        self.assertEqual(status, 200)
        self.assertEqual(headers["X-Upstream"], "head-ok")
        self.assertEqual(body, b"")
        status, headers, _ = self._request("GET", "/redirect")
        self.assertEqual(status, 307)
        self.assertEqual(headers["Location"], "/destination")
        records = self._flow_records()
        methods = {(r["request"]["method"], r["response"]["status_code"]) for r in records if r.get("response")}
        self.assertTrue({("GET", 200), ("PUT", 200), ("HEAD", 200), ("GET", 307)}.issubset(methods))

    def test_auth_challenge_round_trip_keeps_one_client_connection(self) -> None:
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        self.addCleanup(conn.close)
        target = f"http://localhost:{self.upstream_port}/challenge"
        conn.request("GET", target)
        first = conn.getresponse()
        self.assertEqual(first.status, 401)
        self.assertIn("NTLM", first.getheader("WWW-Authenticate", ""))
        first.read()
        conn.request("GET", target, headers={"Authorization": "NTLM synthetic-response"})
        second = conn.getresponse()
        self.assertEqual(second.status, 401)
        second.read()
        with FakeUpstreamHandler.lock:
            auth_values = [auth for method, _, auth in FakeUpstreamHandler.received if method == "GET"]
        self.assertIn("NTLM synthetic-response", auth_values)
        records = self._flow_records()
        self.assertGreaterEqual(sum(1 for r in records if r["request"]["path"].endswith("challenge")), 2)
        challenge_records = [r for r in records if r["request"]["path"].endswith("challenge")]
        self.assertTrue(all(r["request"]["client_connection_id"] for r in challenge_records))
        self.assertEqual(challenge_records[0]["request"]["client_connection_id"],
                         challenge_records[1]["request"]["client_connection_id"])
        self.assertEqual(challenge_records[0]["response"]["server_connection_id"],
                         challenge_records[1]["response"]["server_connection_id"])
        separate = http.client.HTTPConnection("127.0.0.1", self.port, timeout=5)
        self.addCleanup(separate.close)
        separate.request("GET", target)
        response = separate.getresponse()
        response.read()
        records = self._flow_records()
        challenge_records = [r for r in records if r["request"]["path"].endswith("challenge")]
        self.assertGreaterEqual(len(challenge_records), 3, f"expected a third isolated request; records: {records!r}")
        self.assertNotEqual(challenge_records[0]["request"]["client_connection_id"],
                            challenge_records[-1]["request"]["client_connection_id"],
                            f"client connection IDs were not distinct: {records!r}")
        self.assertNotEqual(challenge_records[0]["response"]["server_connection_id"],
                            challenge_records[-1]["response"]["server_connection_id"])

    def test_non_allowlisted_host_does_not_reach_upstream(self) -> None:
        before = len(FakeUpstreamHandler.received)
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=3)
        self.addCleanup(conn.close)
        conn.request("GET", f"http://not-allowed.invalid:{self.upstream_port}/blocked")
        with self.assertRaises((OSError, http.client.HTTPException)):
            conn.getresponse()
        with FakeUpstreamHandler.lock:
            self.assertEqual(len(FakeUpstreamHandler.received), before)

    def test_capture_limit_marks_truncation_without_changing_transfer(self) -> None:
        # This test restarts the process with a tiny capture limit while leaving
        # the wire payload larger than the limit.
        self._stop_proxy()
        self.output_root = Path(self.temp.name) / "limited"
        self.port = self._free_port()
        args = [PROXY_PYTHON, str(RUNNER), "--hosts", "localhost,127.0.0.1", "--port", str(self.port),
                "--output-root", str(self.output_root), "--name", "limited", "--max-body-bytes", "8",
                "--max-total-bytes", "32"]
        creationflags = ((getattr(subprocess, "CREATE_NO_WINDOW", 0) |
                          getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)) if os.name == "nt" else 0)
        self.proc = subprocess.Popen(args, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                     stderr=subprocess.STDOUT, creationflags=creationflags)
        self._wait_ready()
        payload = bytes(range(256))
        status, _, response = self._request("POST", "/large", payload)
        self.assertEqual(status, 200)
        self.assertEqual(response, b"upstream:" + payload)
        record = next(r for r in self._flow_records() if r["request"]["path"].endswith("/large"))
        meta = record["request_body"]
        self.assertEqual(meta["original_bytes"], len(payload))
        self.assertEqual(meta["captured_bytes"], 8)
        self.assertFalse(meta["complete"])
        self.assertEqual((self._run_dir() / meta["file"]).read_bytes(), payload[:8])

    def test_https_connect_uses_upstream_ca_and_client_trusts_only_run_ca(self) -> None:
        self._exercise_https()

    def test_rejected_proxy_ca_keeps_run_incomplete_after_successful_request(self) -> None:
        self._exercise_https(reject_proxy_ca=True)

    def test_reverse_https_uses_supplied_certificate_and_records_binary(self) -> None:
        self._exercise_https(reverse=True)

    def test_reverse_https_client_to_http_upstream_preserves_host_and_binary(self) -> None:
        self._exercise_https(reverse=True, upstream_http=True)

    def _exercise_https(self, *, reject_proxy_ca: bool = False, reverse: bool = False,
                        upstream_http: bool = False) -> None:
        now = datetime.now(timezone.utc)
        ca_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Capture test CA")])
        ca_cert = (x509.CertificateBuilder().subject_name(ca_name).issuer_name(ca_name)
                   .public_key(ca_key.public_key()).serial_number(x509.random_serial_number())
                   .not_valid_before(now - timedelta(minutes=1))
                   .not_valid_after(now + timedelta(days=1))
                   .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
                   .add_extension(x509.SubjectKeyIdentifier.from_public_key(ca_key.public_key()), critical=False)
                   .add_extension(x509.KeyUsage(digital_signature=False, content_commitment=False,
                       key_encipherment=False, data_encipherment=False, key_agreement=False,
                       key_cert_sign=True, crl_sign=True, encipher_only=False, decipher_only=False), critical=True)
                   .sign(ca_key, hashes.SHA256()))
        leaf_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        leaf_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
        leaf_cert = (x509.CertificateBuilder().subject_name(leaf_name).issuer_name(ca_name)
                     .public_key(leaf_key.public_key()).serial_number(x509.random_serial_number())
                     .not_valid_before(now - timedelta(minutes=1))
                     .not_valid_after(now + timedelta(days=1))
                     .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()), critical=False)
                     .add_extension(x509.SubjectAlternativeName([
                         x509.DNSName("localhost"), x509.DNSName("sharepoint.dev.localhost")]), critical=False)
                     .sign(ca_key, hashes.SHA256()))
        ca_path = Path(self.temp.name) / "upstream-ca.pem"
        ca_path.write_bytes(ca_cert.public_bytes(serialization.Encoding.PEM))
        cert_path = Path(self.temp.name) / "upstream-cert.pem"
        cert_path.write_bytes(leaf_cert.public_bytes(serialization.Encoding.PEM))
        key_path = Path(self.temp.name) / "upstream-key.pem"
        key_path.write_bytes(leaf_key.private_bytes(serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))

        tls_server = ThreadingHTTPServer(("127.0.0.1", 0), FakeUpstreamHandler)
        tls_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls_context.load_cert_chain(cert_path, key_path)
        upstream_sni = []
        tls_context.set_servername_callback(lambda sock, name, context: upstream_sni.append(name))
        if not upstream_http:
            tls_server.socket = tls_context.wrap_socket(tls_server.socket, server_side=True)
        server_thread = threading.Thread(target=tls_server.serve_forever, daemon=True)
        server_thread.start()
        self.addCleanup(tls_server.server_close)
        self.addCleanup(server_thread.join, 3)
        self.addCleanup(tls_server.shutdown)

        self._stop_proxy()
        self.output_root = Path(self.temp.name) / "tls-captures"
        self.port = self._free_port()
        args = [PROXY_PYTHON, str(RUNNER), "--hosts", "localhost", "--port", str(self.port),
                "--output-root", str(self.output_root), "--name", "tls", "--max-body-bytes", "1048576",
                "--max-total-bytes", "4194304", "--upstream-ca", str(ca_path)]
        if reverse:
            upstream_scheme = "http" if upstream_http else "https"
            args.extend(["--reverse-upstream", f"{upstream_scheme}://localhost:{tls_server.server_port}",
                         "--tls-cert", str(cert_path), "--tls-key", str(key_path)])
            if upstream_http:
                # A stale HTTPS CA setting must not prevent an HTTP capture.
                args.extend(["--upstream-ca", str(Path(self.temp.name) / "missing.pem")])
        flags = ((getattr(subprocess, "CREATE_NO_WINDOW", 0) |
                  getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)) if os.name == "nt" else 0)
        self.proc = subprocess.Popen(args, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                     stderr=subprocess.STDOUT, creationflags=flags)
        self._wait_ready()
        proxy_ca = self.output_root / ".mitmproxy" / "mitmproxy-ca-cert.pem"
        deadline = time.monotonic() + 10
        while not proxy_ca.exists() and time.monotonic() < deadline:
            time.sleep(0.05)
        self.assertTrue(proxy_ca.exists(), "mitmproxy run CA was not generated")
        client_context = ssl.create_default_context(cafile=str(ca_path if reverse else proxy_ca))
        if reject_proxy_ca:
            with self.assertRaises(ssl.SSLCertVerificationError):
                self._connect_tls(tls_server.server_port, ssl.create_default_context())
        if reverse:
            tls_conn = client_context.wrap_socket(
                socket.create_connection(("127.0.0.1", self.port), timeout=5), server_hostname="sharepoint.dev.localhost")
            self.assertEqual(tls_conn.getpeercert(binary_form=True), leaf_cert.public_bytes(serialization.Encoding.DER))
        else:
            tls_conn = self._connect_tls(tls_server.server_port, client_context)
        self.addCleanup(tls_conn.close)
        request_host = f"sharepoint.dev.localhost:{self.port}" if reverse else f"localhost:{tls_server.server_port}"
        tls_conn.sendall(f"POST /tls HTTP/1.1\r\nHost: {request_host}\r\nContent-Length: 10\r\nConnection: close\r\n\r\ntls-\x00-body".encode("latin-1"))
        response = http.client.HTTPResponse(tls_conn)
        response.begin()
        self.assertEqual(response.status, 200)
        self.assertEqual(response.read(), b"upstream:tls-\x00-body")
        records = self._flow_records()
        expected_scheme = "http" if upstream_http else "https"
        self.assertTrue(any(r["request"]["scheme"] == expected_scheme and r["request"]["path"].endswith("/tls") for r in records))
        if reverse:
            record = next(r for r in records if r["request"]["path"] == "/tls")
            if upstream_http:
                self.assertEqual(upstream_sni, [])
            else:
                self.assertTrue(upstream_sni)
                self.assertTrue(all(name == "localhost" for name in upstream_sni))
            self.assertEqual((self._run_dir() / record["request_body"]["file"]).read_bytes(), b"tls-\x00-body")
            self.assertEqual((self._run_dir() / record["response_body"]["file"]).read_bytes(), b"upstream:tls-\x00-body")
            self.assertEqual(dict(record["request"]["headers"])["Host"],
                             f"sharepoint.dev.localhost:{tls_server.server_port}")
            self.assertEqual(record["request"]["original_host_header"], request_host)
        # Exercise WebSocket upgrade through the same TLS trust path, including
        # both reverse listener variants. The test peers parse RFC 6455 frames.
        if reverse:
            ws_sock = client_context.wrap_socket(
                socket.create_connection(("127.0.0.1", self.port), timeout=5), server_hostname="sharepoint.dev.localhost")
        else:
            ws_sock = self._connect_tls(tls_server.server_port, client_context)
        ws_client = websocket_test_peer.Client(self.port, tls_server.server_port, sock=ws_sock, host=request_host)
        self.addCleanup(ws_client.close)
        ws_client.send(2, b"tls-websocket-\x00\xff")
        self.assertEqual(ws_client.receive(), (2, b"tls-websocket-\x00\xff"))
        ws_client.finish()
        self._stop_proxy()
        ws_records = self._websocket_records()
        self.assertEqual(len(ws_records), 1)
        self.assertEqual(ws_records[0]["websocket"]["recorded_messages"], 2)
        if reject_proxy_ca:
            from tools.capture.inspect_capture import validate_run
            manifest = json.loads((self._run_dir() / "manifest.json").read_text("utf-8"))
            self.assertIn("tls_failed_client", manifest["errors"])
            self.assertEqual(manifest["status"], "incomplete")
            self.assertTrue(validate_run(self._run_dir())[1])
        else:
            self._validate_run()

    def test_https_upstream_certificate_validation_fails_without_ca(self) -> None:
        now = datetime.now(timezone.utc)
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
        cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
                .serial_number(x509.random_serial_number())
                .not_valid_before(now - timedelta(minutes=1))
                .not_valid_after(now + timedelta(days=1))
                .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost")]), critical=False)
                .sign(key, hashes.SHA256()))
        cert_path = Path(self.temp.name) / "untrusted-cert.pem"
        key_path = Path(self.temp.name) / "untrusted-key.pem"
        cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
        key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        tls_server = ThreadingHTTPServer(("127.0.0.1", 0), FakeUpstreamHandler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(cert_path, key_path)
        tls_server.socket = context.wrap_socket(tls_server.socket, server_side=True)
        thread = threading.Thread(target=tls_server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(tls_server.server_close)
        self.addCleanup(thread.join, 3)
        self.addCleanup(tls_server.shutdown)
        self._stop_proxy()
        self.output_root = Path(self.temp.name) / "untrusted-captures"
        self.port = self._free_port()
        args = [PROXY_PYTHON, str(RUNNER), "--hosts", "localhost", "--port", str(self.port),
                "--output-root", str(self.output_root), "--name", "tls-untrusted"]
        flags = ((getattr(subprocess, "CREATE_NO_WINDOW", 0) |
                  getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)) if os.name == "nt" else 0)
        self.proc = subprocess.Popen(args, cwd=ROOT, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                     stderr=subprocess.STDOUT, creationflags=flags)
        self._wait_ready()
        proxy_ca = self.output_root / ".mitmproxy" / "mitmproxy-ca-cert.pem"
        deadline = time.monotonic() + 10
        while not proxy_ca.exists() and time.monotonic() < deadline:
            time.sleep(0.05)
        client_context = ssl.create_default_context(cafile=str(proxy_ca))
        tls_conn = self._connect_tls(tls_server.server_port, client_context)
        self.addCleanup(tls_conn.close)
        tls_conn.sendall(f"GET /untrusted HTTP/1.1\r\nHost: localhost:{tls_server.server_port}\r\nConnection: close\r\n\r\n".encode())
        response = http.client.HTTPResponse(tls_conn)
        response.begin()
        self.assertGreaterEqual(response.status, 500)
        response.read()


if __name__ == "__main__":
    unittest.main()
