"""mitmproxy addon that records allow-listed HTTP and WebSocket traffic.

Bodies are tee'd from mitmproxy's streaming callbacks. Capture limits affect only
the evidence written to disk; callbacks always return the original bytes.
"""

from __future__ import annotations

import hashlib
import ipaddress
import json
import os
import platform
import re
import threading
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

from private_files import private_directory, private_open, private_write


REDACTED_HEADERS = {"authorization", "proxy-authorization", "www-authenticate", "proxy-authenticate", "cookie", "set-cookie"}
URL_HEADERS = {"location", "content-location", "referer", "destination"}


def _redact_url(value: str) -> str:
    """Remove URL credentials from metadata without changing forwarded traffic."""
    path, fragment, _ = value.partition("#")
    path, query, _ = path.partition("?")
    path = re.sub(r"^((?:[A-Za-z][A-Za-z0-9+.-]*:)?//)[^/]*@", r"\1[REDACTED]@", path)
    return path + ("?[REDACTED]" if query else "") + ("#[REDACTED]" if fragment else "")


def _query_parameter_names(value: str) -> list[str]:
    """Return bounded ordinary query names without decoding or retaining values."""
    path, separator, query = value.partition("?")
    if not separator:
        return []
    query = query.partition("#")[0]
    if not query or len(query) > 8192:
        return []

    names: list[str] = []
    seen: set[str] = set()
    for segment in query.split("&")[:64]:
        if "=" not in segment:
            continue
        name = segment.split("=", 1)[0]
        if len(name) > 64 or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*", name):
            continue
        lower = name.lower()
        if (re.fullmatch(r"[0-9a-f]{16,}", lower)
                or re.fullmatch(r"[0-9a-f]{8}-[0-9a-f-]{27,}", lower)
                or (len(name) >= 16 and any(c.islower() for c in name)
                    and any(c.isupper() for c in name) and any(c.isdigit() for c in name)
                    and "_" not in name and "." not in name and "-" not in name)
                or (len(name) >= 24 and "_" not in name and "." not in name and "-" not in name)):
            continue
        if lower not in seen:
            names.append(name)
            seen.add(lower)
            if len(names) == 32:
                break
    return names


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _replace_file(source: Path, target: Path) -> None:
    # Windows readers/scanners may briefly hold a file without delete sharing.
    # Bound the delay since these writes run on mitmproxy's event loop.
    delays = (0.01, 0.03, 0.1)
    for attempt in range(len(delays) + 1):
        try:
            os.replace(source, target)
            return
        except PermissionError as ex:
            if getattr(ex, "winerror", None) not in (5, 32, 33) or attempt == len(delays):
                raise
            time.sleep(delays[attempt])


def _atomic_json(path: Path, value: Any) -> None:
    private_directory(path.parent)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with private_open(temp, "w", encoding="utf-8", newline="\n") as f:
            json.dump(value, f, ensure_ascii=False, indent=2)
            f.write("\n")
            f.flush()
            os.fsync(f.fileno())
        _replace_file(temp, path)
    finally:
        temp.unlink(missing_ok=True)


def _headers(headers: Any) -> list[list[str]]:
    result = []
    # mitmproxy Headers.fields preserves the original field order and duplicates.
    fields = getattr(headers, "fields", None)
    if fields is not None:
        source = fields
    else:
        source = headers.items(multi=True)
    for name, value in source:
        if isinstance(name, bytes):
            name = name.decode("latin-1")
        if isinstance(value, bytes):
            value = value.decode("latin-1")
        name, value = str(name), str(value)
        if name.lower() in REDACTED_HEADERS:
            value = "[REDACTED]"
        elif name.lower() in URL_HEADERS:
            value = _redact_url(value)
        result.append([name, value])
    return result


class BodySink:
    """Bounded streaming body writer; retains no body chunks in memory."""

    def __init__(self, addon: "CaptureAddon", flow_id: str, kind: str, limit: int | None = None):
        self.addon, self.flow_id, self.kind = addon, flow_id, kind
        self.limit = addon.max_body if limit is None else limit
        self.path = addon.root / "files" / f"{flow_id}.{kind}.body.bin"
        self.temp = self.path.with_name(self.path.name + ".part")
        self.digest = hashlib.sha256()
        self.original_bytes = 0
        self.captured_bytes = 0
        self.complete = True
        self.error: str | None = None
        self.closed = False
        self.file = None
        try:
            private_directory(self.path.parent)
            self.file = private_open(self.temp)
        except OSError as ex:
            self._fail(ex)

    def feed(self, chunk: bytes) -> bytes:
        data = bytes(chunk)
        self.original_bytes += len(data)
        self.digest.update(data)
        room_body = max(0, self.limit - self.captured_bytes)
        room_total = max(0, self.addon.max_total - self.addon.total_captured)
        portion = data[: min(len(data), room_body, room_total)]
        if len(portion) != len(data):
            self.complete = False
        if portion and self.file is not None:
            try:
                self.file.write(portion)
                self.captured_bytes += len(portion)
                self.addon.total_captured += len(portion)
            except OSError as ex:
                self._fail(ex)
        elif portion:
            self.complete = False
        return data

    def _fail(self, ex: OSError) -> None:
        self.complete = False
        self.error = type(ex).__name__
        self.addon._io_error(ex, "body", self.path)
        if self.file is not None:
            try:
                self.file.close()
            except OSError:
                pass
            self.file = None

    def finish(self) -> dict[str, Any]:
        if not self.closed:
            self.closed = True
            if self.file is not None:
                try:
                    self.file.flush()
                    os.fsync(self.file.fileno())
                    self.file.close()
                    self.file = None
                    _replace_file(self.temp, self.path)
                except OSError as ex:
                    self._fail(ex)
            if self.original_bytes == 0 and self.file is None and not self.error and not self.path.exists():
                # Empty bodies are valid and get a real empty file.
                try:
                    private_directory(self.path.parent)
                    private_write(self.path, b"")
                except OSError as ex:
                    self._fail(ex)
        return {
            "file": str(self.path.relative_to(self.addon.root)).replace("\\", "/") if self.path.exists() else None,
            "sha256": self.digest.hexdigest(),
            "original_bytes": self.original_bytes,
            "captured_bytes": self.captured_bytes,
            "complete": self.complete and self.captured_bytes == self.original_bytes and self.error is None,
            "error": self.error,
        }


class CaptureAddon:
    def __init__(self, env: dict[str, str] | None = None):
        env = os.environ if env is None else env
        output = env.get("CAPTURE_OUTPUT", "")
        hosts = [h.strip().rstrip(".").lower() for h in env.get("CAPTURE_HOSTS", "").split(",") if h.strip()]
        if not output or not Path(output).is_absolute():
            raise ValueError("CAPTURE_OUTPUT must be an absolute directory")
        if not hosts:
            raise ValueError("CAPTURE_HOSTS must contain at least one exact hostname")
        self.root = Path(output)
        self.hosts = sorted(set(hosts))
        self.reverse_upstream = env.get("CAPTURE_REVERSE_UPSTREAM") or None
        local_processes = env.get("CAPTURE_LOCAL_PROCESSES") or ""
        self.local_processes = local_processes.split(",") if local_processes else []
        self.http2 = env.get("CAPTURE_HTTP2") == "true"
        self.max_body = int(env.get("CAPTURE_MAX_BODY_BYTES", 32 * 1024 * 1024))
        self.max_total = int(env.get("CAPTURE_MAX_TOTAL_BYTES", 512 * 1024 * 1024))
        self.max_flows = int(env.get("CAPTURE_MAX_FLOWS", "10000"))
        self.max_ws_message = int(env.get("CAPTURE_MAX_WEBSOCKET_MESSAGE_BYTES", 32 * 1024 * 1024))
        self.max_ws_messages = int(env.get("CAPTURE_MAX_WEBSOCKET_MESSAGES", "10000"))
        if min(self.max_body, self.max_total, self.max_ws_message) < 0 or min(self.max_flows, self.max_ws_messages) < 1:
            raise ValueError("capture byte limits must be non-negative")
        self.lock = threading.RLock()
        self.total_captured = 0
        self.next_sequence = 1
        self.next_ws_event = 1
        self.ws_message_count = 0
        self.session_id = str(uuid.uuid4())
        self.exchanges: dict[str, dict[str, Any]] = {}
        self.flow_ids: dict[str, str] = {}
        self.sinks: dict[tuple[str, str], BodySink] = {}
        self.recording_healthy = True
        self.recording_errors: list[str] = []
        self.recording_error_details: list[dict[str, Any]] = []
        self.transport_error_details: list[dict[str, Any]] = []
        self.terminal_status: str | None = None
        self.started_at = _now()
        private_directory(self.root)
        private_directory(self.root / "flows")
        private_directory(self.root / "files")
        if (self.root / "manifest.json").exists():
            raise ValueError("CAPTURE_OUTPUT already contains a manifest; use a new run directory")
        self._write_manifest("running")

    def _recording_error(self, error: str) -> None:
        with self.lock:
            self.recording_healthy = False
            if error not in self.recording_errors and len(self.recording_errors) < 32:
                self.recording_errors.append(error)
                try:
                    from mitmproxy import ctx
                    ctx.log.warning(f"Capture recording issue: {error}")
                except Exception:
                    pass

    def _io_error(self, ex: OSError, operation: str, path: Path) -> None:
        # Keep payloads and absolute user paths out of diagnostics.
        detail = {
            "error": type(ex).__name__, "operation": operation,
            "file": path.relative_to(self.root).as_posix(),
            "errno": ex.errno, "winerror": getattr(ex, "winerror", None),
        }
        if len(self.recording_error_details) < 32:
            self.recording_error_details.append(detail)
            print(f"Capture filesystem error: {json.dumps(detail)}", flush=True)
        self._recording_error(type(ex).__name__)

    @staticmethod
    def _engine_version() -> str:
        try:
            from importlib.metadata import version
            return version("mitmproxy")
        except Exception:
            return "unknown"

    def _write_manifest(self, status: str) -> None:
        if self.terminal_status is not None:
            # Mitmproxy may dispatch final flow/WebSocket hooks after DoneHook.
            # Keep shutdown terminal, and only downgrade a provisional complete
            # result when a late hook reveals an error or incomplete flow.
            if self.terminal_status == "complete" and (
                not self.recording_healthy
                or any(x.get("completeness") != "complete" or
                       (x.get("websocket_expected") and x.get("websocket", {}).get("completeness") != "complete")
                       for x in self.exchanges.values())
            ):
                self.terminal_status = "incomplete"
            status = self.terminal_status
        manifest = {
            "schema_version": 2,
            "session_id": self.session_id,
            "websocket_capture_scope": "completed_application_messages",
            "engine": "mitmproxy",
            "engine_version": self._engine_version(),
            "python_version": platform.python_version(),
            "status": status,
            "started_at": self.started_at,
            "updated_at": _now(),
            "allowed_hosts": self.hosts,
            "proxy_mode": "local" if self.local_processes else "reverse" if self.reverse_upstream else "forward",
            "local_processes": self.local_processes,
            "http2": self.http2,
            "reverse_upstream": self.reverse_upstream,
            "flow_count": len(self.exchanges),
            "pending_count": sum(1 for x in self.exchanges.values() if x.get("completeness") == "pending" or x.get("websocket", {}).get("completeness") == "pending"),
            "websocket_count": sum(1 for x in self.exchanges.values() if "websocket" in x),
            "websocket_message_count": self.ws_message_count,
            "websocket_observed_messages": sum(x.get("websocket", {}).get("observed_messages", 0) for x in self.exchanges.values()),
            "websocket_dropped_messages": sum(x.get("websocket", {}).get("dropped_messages", 0) for x in self.exchanges.values()),
            "recording_healthy": self.recording_healthy,
            "errors": self.recording_errors,
            "recording_errors": self.recording_errors,
            "recording_error_details": self.recording_error_details,
            "transport_error_details": self.transport_error_details,
            "captured_body_bytes": self.total_captured,
            "limits": {"max_body_bytes": self.max_body, "max_total_bytes": self.max_total,
                       "max_flows": self.max_flows, "max_websocket_message_bytes": self.max_ws_message,
                       "max_websocket_messages": self.max_ws_messages},
        }
        try:
            _atomic_json(self.root / "manifest.json", manifest)
        except OSError as ex:
            self._io_error(ex, "manifest_write", self.root / "manifest.json")

    def _save_flow(self, flow_id: str) -> None:
        try:
            _atomic_json(self.root / "flows" / f"{flow_id}.json", self.exchanges[flow_id])
        except OSError as ex:
            self._io_error(ex, "flow_write", self.root / "flows" / f"{flow_id}.json")
            self._write_manifest("running")

    @staticmethod
    def _host(flow: Any) -> str:
        request = flow.request
        return str(getattr(request, "host", "")).rstrip(".").lower()

    def _allowed(self, flow: Any) -> bool:
        host = self._host(flow)
        if self.local_processes:
            # With transparent interception mitmproxy may set request.host to
            # the resolved server IP. The decoded authority/Host and client
            # SNI carry the DNS identity selected by the Office process.
            names = self._local_dns_names(flow)
            return bool(names) and len(set(names)) == 1 and all(name in self.hosts for name in names)
        return host in self.hosts

    @staticmethod
    def _dns_name(value: str) -> str:
        if isinstance(value, bytes):
            try:
                value = value.decode("idna", "strict")
            except UnicodeError:
                return ""
        value = value.strip()
        if not value or "://" in value:
            return ""
        try:
            parsed = urlsplit("//" + value)
            if parsed.username is not None or parsed.password is not None or parsed.path or parsed.query or parsed.fragment:
                return ""
            _ = parsed.port  # Reject malformed ports even though matching is host-only.
            host = (parsed.hostname or "").rstrip(".").lower()
        except ValueError:
            return ""
        if not host:
            return ""
        try:
            ipaddress.ip_address(host)
            return ""
        except ValueError:
            pass
        return host if re.fullmatch(r"[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?", host) else ""

    @classmethod
    def _local_dns_names(cls, flow: Any) -> list[str]:
        request = flow.request
        values = [
            getattr(getattr(flow, "client_conn", None), "sni", "") or "",
            getattr(request, "authority", "") or "",
            getattr(request, "host_header", "") or "",
            getattr(request, "host", "") or "",
        ]
        names = []
        for value in values:
            if not value:
                continue
            name = cls._dns_name(str(value))
            if not name:
                try:
                    ipaddress.ip_address(str(value).strip("[]"))
                    continue
                except ValueError:
                    pass
                return []
            names.append(name)
        return names

    def _capture_host(self, flow: Any) -> str:
        if self.local_processes:
            names = self._local_dns_names(flow)
            if names and len(set(names)) == 1:
                return names[0]
        return self._host(flow)

    def _destination_ip(self, flow: Any) -> str | None:
        if not self.local_processes:
            return None
        try:
            return str(ipaddress.ip_address(self._host(flow)))
        except ValueError:
            return None

    def _transport_detail(self, category: str, host: str = "", port: Any = None,
                          sni: str = "", authority: str = "", host_header: str = "",
                          cause: str = "") -> None:
        """Record only the failed network destination, never TLS or request contents."""
        safe_host = host.rstrip(".").lower()
        if safe_host and not re.fullmatch(r"[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?", safe_host):
            safe_host = "[unavailable]"
        if not isinstance(port, int) or not 0 < port <= 65535:
            port = None
        detail = {"error": category, "host": safe_host or "[unavailable]", "port": port}
        for key, value in (("sni", sni), ("authority", authority), ("host_header", host_header)):
            if value:
                detail[key] = self._dns_name(value) or "[unavailable]"
        if cause:
            detail["cause"] = cause
        with self.lock:
            if detail not in self.transport_error_details and len(self.transport_error_details) < 32:
                self.transport_error_details.append(detail)

    def requestheaders(self, flow: Any) -> None:
        if not self._allowed(flow):
            self._recording_error("destination_not_allowlisted")
            request = flow.request
            self._transport_detail(
                "destination_not_allowlisted", self._host(flow), getattr(request, "port", None),
                getattr(getattr(flow, "client_conn", None), "sni", "") or "",
                getattr(request, "authority", "") or "", getattr(request, "host_header", "") or "")
            self._write_manifest("running")
            flow.kill()
            return
        original_host_header = flow.request.host_header
        if self.reverse_upstream and original_host_header:
            upstream = urlsplit(self.reverse_upstream)
            hostname = urlsplit("//" + original_host_header).hostname
            if hostname:
                hostname = f"[{hostname}]" if ":" in hostname else hostname
                port = upstream.port or (443 if upstream.scheme == "https" else 80)
                flow.request.host_header = f"{hostname}:{port}"
        if len(self.exchanges) >= self.max_flows:
            self._recording_error("max_flows_reached")
            # Stop recording, but preserve forwarding for an allow-listed target.
            flow.request.stream = True
            self._write_manifest("running")
            return
        fid = str(uuid.uuid4())
        flow_id = str(getattr(flow, "id", ""))
        if not flow_id:
            self._recording_error("flow_id_missing")
            flow.request.stream = True
            return
        self.flow_ids[flow_id] = fid
        req = flow.request
        conn = getattr(flow, "client_conn", None)
        self.exchanges[fid] = {
            "schema_version": 2,
            "session_id": self.session_id,
            "flow_id": fid,
            "mitmproxy_flow_id": flow_id,
            "sequence": self.next_sequence,
            "completeness": "pending",
            "request": {
                "timestamp": _now(), "method": str(getattr(req, "method", "")),
                "scheme": str(getattr(req, "scheme", "")), "host": self._capture_host(flow),
                "port": getattr(req, "port", None), "path": _redact_url(str(getattr(req, "path", ""))),
                "query_parameter_names": _query_parameter_names(str(getattr(req, "path", ""))),
                "http_version": str(getattr(req, "http_version", "")), "headers": _headers(req.headers),
                "original_host_header": original_host_header,
                "client_connection_id": str(getattr(conn, "id", "")),
            },
        }
        destination_ip = self._destination_ip(flow)
        if destination_ip:
            self.exchanges[fid]["request"]["destination_ip"] = destination_ip
        self.next_sequence += 1
        sink = BodySink(self, fid, "request")
        self.sinks[(fid, "request")] = sink
        req.stream = sink.feed
        self._save_flow(fid)
        self._write_manifest("running")

    def http_connect(self, flow: Any) -> None:
        """Reject disallowed CONNECT targets before mitmproxy opens upstream."""
        if not self._allowed(flow):
            self._recording_error("destination_not_allowlisted")
            self._transport_detail("destination_not_allowlisted", self._host(flow), getattr(flow.request, "port", None))
            self._write_manifest("running")
            flow.kill()

    def server_connect(self, data: Any) -> None:
        if self.reverse_upstream and urlsplit(self.reverse_upstream).scheme == "https":
            # keep_host_header also disables mitmproxy's default reverse SNI.
            # HTTP Host selects the SharePoint zone; TLS verifies the real server.
            data.server.sni = urlsplit(self.reverse_upstream).hostname

    @staticmethod
    def _tls_failure_cause(error: Any, *, upstream: bool) -> str:
        """Map engine errors to fixed labels; never persist raw TLS exception text."""
        message = str(error or "").lower()
        # mitmproxy wraps a plain disconnect in text that mentions certificate
        # trust. Match transport closure before considering certificate alerts.
        if any(token in message for token in ("connection closed", "disconnected during the handshake",
                                               "unexpected eof", "eof occurred", "connection reset",
                                               "broken pipe", "forcibly closed")):
            return "connection_closed"
        if any(token in message for token in ("unknown ca", "unknown_ca", "bad certificate",
                                               "certificate unknown")):
            return "upstream_certificate_rejected" if upstream else "client_unknown_ca"
        if any(token in message for token in ("certificate verify failed", "certificate verification failed",
                                               "verify error", "alert certificate")):
            return "upstream_certificate_rejected" if upstream else "client_certificate_rejected"
        if any(token in message for token in ("timed out", "timeout")):
            return "timeout"
        if any(token in message for token in ("eof", "closed", "reset", "broken pipe")):
            return "connection_closed"
        if any(token in message for token in ("handshake", "alert", "protocol version", "wrong version")):
            return "handshake_failure"
        return "other"

    def _transport_failure(self, category: str, host: str = "", port: Any = None, cause: str = "") -> None:
        # Handshake failures can occur before requestheaders creates a flow.
        self._recording_error(category)
        self._transport_detail(category, host, port, cause=cause)
        self._write_manifest("running")

    def tls_failed_client(self, data: Any) -> None:
        context = getattr(data, "context", None)
        client = getattr(context, "client", None)
        server = getattr(context, "server", None)
        address = getattr(server, "address", None)
        conn = getattr(data, "conn", None)
        self._transport_failure("tls_failed_client", getattr(client, "sni", "") or "",
                                address[1] if isinstance(address, tuple) and len(address) > 1 else None,
                                self._tls_failure_cause(getattr(conn, "error", None) or getattr(data, "error", None),
                                                        upstream=False))

    def tls_failed_server(self, data: Any) -> None:
        conn = getattr(data, "conn", None)
        address = getattr(conn, "address", None)
        self._transport_failure("tls_failed_server",
                                address[0] if isinstance(address, tuple) and address else "",
                                address[1] if isinstance(address, tuple) and len(address) > 1 else None,
                                self._tls_failure_cause(getattr(conn, "error", None) or getattr(data, "error", None),
                                                        upstream=True))

    def server_connect_error(self, data: Any) -> None:
        self._transport_failure("server_connect_error")

    def http_connect_error(self, flow: Any) -> None:
        self._transport_failure("http_connect_error")

    def request(self, flow: Any) -> None:
        fid = self.flow_ids.get(str(getattr(flow, "id", "")))
        if fid and getattr(flow.request, "raw_content", None) and self.sinks[(fid, "request")].original_bytes == 0:
            self.sinks[(fid, "request")].feed(flow.request.raw_content)
        if fid:
            trailers = getattr(flow.request, "trailers", None)
            self.exchanges[fid]["request"]["trailers"] = _headers(trailers) if trailers else []
            self._close_body(fid, "request")

    def responseheaders(self, flow: Any) -> None:
        fid = self.flow_ids.get(str(getattr(flow, "id", "")))
        if not fid or not getattr(flow, "response", None):
            if self._allowed(flow) and getattr(flow, "response", None):
                flow.response.stream = True
            return
        response = flow.response
        self.exchanges[fid]["response"] = {
            "timestamp": _now(), "status_code": getattr(response, "status_code", None),
            "http_version": str(getattr(response, "http_version", "")), "headers": _headers(response.headers),
            "server_connection_id": str(getattr(getattr(flow, "server_conn", None), "id", "")),
        }
        self._save_flow(fid)
        flow.response.stream = self._sink(fid, "response").feed

    def response(self, flow: Any) -> None:
        fid = self.flow_ids.get(str(getattr(flow, "id", "")))
        if not fid:
            return
        response = flow.response
        if getattr(flow, "websocket", None) is not None:
            self.exchanges[fid]["websocket_expected"] = True
        if response is not None:
            trailers = getattr(response, "trailers", None)
            self.exchanges[fid]["response"]["trailers"] = _headers(trailers) if trailers else []
            sink = self._sink(fid, "response")
            if getattr(response, "raw_content", None) and sink.original_bytes == 0:
                sink.feed(response.raw_content)
            self._close_body(fid, "response")
        self._complete(fid, flow)

    def _ws_event(self) -> dict[str, Any]:
        event = {"event_sequence": self.next_ws_event, "timestamp": _now()}
        self.next_ws_event += 1
        return event

    def websocket_start(self, flow: Any) -> None:
        fid = self.flow_ids.get(str(flow.id))
        if not fid:
            return  # HTTP flow limit already marks this run unhealthy.
        self.exchanges[fid]["websocket_expected"] = True
        self.exchanges[fid]["websocket"] = {
            "start": self._ws_event(), "completeness": "pending",
            "observed_messages": 0, "recorded_messages": 0, "dropped_messages": 0,
            "original_bytes": 0, "captured_bytes": 0,
        }
        self._save_flow(fid)
        self._write_manifest("running")

    def websocket_message(self, flow: Any) -> None:
        # In 12.2.3 the relay retains the current message in a local variable
        # after this hook. Remove older history without modifying that message
        # or its forwarding flags. This kit runs as the only custom addon.
        message = flow.websocket.messages[-1]
        del flow.websocket.messages[:-1]
        fid = self.flow_ids.get(str(flow.id))
        if not fid:
            return
        ws = self.exchanges[fid].get("websocket")
        if ws is None:
            self._transport_failure("websocket_start_missing")
            return
        ws["observed_messages"] += 1
        ws["original_bytes"] += len(message.content)
        event = self._ws_event()
        if message.dropped or message.injected:
            self._recording_error("websocket_message_modified")
        if self.ws_message_count >= self.max_ws_messages:
            ws["dropped_messages"] += 1
            self._recording_error("max_websocket_messages_reached")
        else:
            number = ws["observed_messages"]
            sink = BodySink(self, fid, f"websocket.{number:08d}", self.max_ws_message)
            sink.feed(message.content)
            payload = sink.finish()
            item = {
                "schema_version": 2, "session_id": self.session_id, "flow_id": fid,
                "sequence": number, **event, "received_at": message.timestamp,
                "direction": "client_to_server" if message.from_client else "server_to_client",
                "type": "text" if message.is_text else "binary",
                "dropped": message.dropped, "injected": message.injected, "payload": payload,
            }
            self.ws_message_count += 1
            ws["recorded_messages"] += 1
            ws["captured_bytes"] += payload["captured_bytes"]
            if not payload["complete"]:
                self._recording_error("websocket_payload_incomplete")
            path = self.root / "websockets" / fid / f"{number:08d}.json"
            try:
                _atomic_json(path, item)
            except OSError as ex:
                self._io_error(ex, "websocket_message_write", path)
        self._save_flow(fid)
        self._write_manifest("running")

    def websocket_end(self, flow: Any) -> None:
        fid = self.flow_ids.get(str(flow.id))
        if not fid:
            return
        ws = self.exchanges[fid].get("websocket")
        if ws is None:
            self._transport_failure("websocket_start_missing")
            return
        data = flow.websocket
        ws["end"] = {
            **self._ws_event(), "closed_at": data.timestamp_end,
            "closed_by_client": data.closed_by_client, "close_code": data.close_code,
            "close_reason": data.close_reason,
            "error": str(flow.error.msg)[:256] if flow.error else None,
        }
        ws["completeness"] = "complete" if (
            data.close_code in (1000, 1001) and not flow.error
            and ws["dropped_messages"] == 0 and ws["original_bytes"] == ws["captured_bytes"]
        ) else "incomplete"
        if data.close_code not in (1000, 1001) or flow.error:
            self._recording_error("websocket_abnormal_end")
        flow.websocket.messages.clear()
        self._save_flow(fid)
        self._write_manifest("running")

    def error(self, flow: Any) -> None:
        fid = self.flow_ids.get(str(getattr(flow, "id", "")))
        if fid:
            err = getattr(flow, "error", None)
            self.exchanges[fid]["error"] = {"timestamp": _now(), "message": str(getattr(err, "msg", "connection error"))[:256]}
            for kind in ("request", "response"):
                if (fid, kind) in self.sinks:
                    if kind == "response":
                        self.sinks[(fid, kind)].complete = False
                    self._close_body(fid, kind)
            self._complete(fid, flow)

    def _sink(self, fid: str, kind: str) -> BodySink:
        key = (fid, kind)
        if key not in self.sinks:
            self.sinks[key] = BodySink(self, fid, kind)
        return self.sinks[key]

    def _close_body(self, fid: str, kind: str) -> None:
        sink = self._sink(fid, kind)
        self.exchanges[fid][f"{kind}_body"] = sink.finish()
        self._save_flow(fid)

    def _complete(self, fid: str, flow: Any) -> None:
        item = self.exchanges[fid]
        bodies_complete = all(item.get(f"{k}_body", {}).get("complete", False) for k in ("request", "response"))
        item["completeness"] = "complete" if bodies_complete and item.get("response") and not item.get("error") else "incomplete"
        item["finished_at"] = _now()
        self._save_flow(fid)
        self._write_manifest("running")
        try:
            req_bytes = item.get("request_body", {}).get("original_bytes", 0)
            resp_bytes = item.get("response_body", {}).get("original_bytes", 0)
            print(f"Captured flow #{item['sequence']}: status={item['completeness']} request_bytes={req_bytes} response_bytes={resp_bytes}", flush=True)
        except Exception:
            pass

    def done(self) -> None:
        stop_task = getattr(self, "stop_task", None)
        if stop_task is not None:
            stop_task.cancel()
        for (fid, kind), sink in list(self.sinks.items()):
            if not sink.closed:
                self.exchanges[fid][f"{kind}_body"] = sink.finish()
                self._save_flow(fid)
        pending = any(x.get("completeness") == "pending" or x.get("websocket", {}).get("completeness") == "pending" for x in self.exchanges.values())
        incomplete = any(x.get("completeness") != "complete" or
                         (x.get("websocket_expected") and x.get("websocket", {}).get("completeness") != "complete")
                         for x in self.exchanges.values())
        status = "complete" if not pending and not incomplete and self.recording_healthy else "incomplete"
        self.terminal_status = status
        self._write_manifest(status)

    async def running(self) -> None:
        """Watch for an Aspire stop request without blocking mitmproxy hooks."""
        import asyncio
        self.stop_task = asyncio.create_task(self._watch_stop())

    async def _watch_stop(self) -> None:
        import asyncio
        from mitmproxy import ctx
        stop_file = self.root / "stop.request"
        while not stop_file.exists():
            await asyncio.sleep(0.25)
        ctx.master.shutdown()


addons = [CaptureAddon()]
