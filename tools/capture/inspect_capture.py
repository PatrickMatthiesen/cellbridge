#!/usr/bin/env python3
"""Validate and inspect an CellBridge capture run (stdlib only)."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import sys
import zlib
import shutil
import xml.etree.ElementTree as ET
from urllib.parse import unquote
from email import policy
from email.parser import BytesParser
from pathlib import Path, PurePosixPath
from datetime import datetime
from typing import Any

MAX_DECOMPRESSED_BYTES = 32 * 1024 * 1024


class CaptureError(Exception):
    pass


def _json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise CaptureError(f"cannot read {path.name}: {exc}") from exc


def _safe_file(run: Path, rel: Any) -> Path:
    if not isinstance(rel, str) or not rel:
        raise CaptureError("body file path is missing")
    posix = PurePosixPath(rel)
    if posix.is_absolute() or ".." in posix.parts or "\\" in rel or re.match(r"^[A-Za-z]:", rel):
        raise CaptureError("body file path is unsafe")
    root = run.resolve()
    target = (run / Path(*posix.parts)).resolve()
    if target != root and root not in target.parents:
        raise CaptureError("body file path escapes capture run")
    return target


def _file_integrity(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def _nonnegative(value: Any) -> bool:
    return type(value) is int and value >= 0


def _timestamp(value: Any) -> bool:
    return type(value) in (int, float) and math.isfinite(value) and value > 0


def _iso_timestamp(value: Any) -> bool:
    try:
        return isinstance(value, str) and datetime.fromisoformat(value).tzinfo is not None
    except ValueError:
        return False


def _validate_payload(run: Path, body: Any, label: str, problems: list[str]) -> int:
    if not isinstance(body, dict):
        raise CaptureError(f"{label} descriptor is missing or invalid")
    if body.get("complete") is not True or body.get("error"):
        problems.append(f"{label} is incomplete")
    path = _safe_file(run, body.get("file"))
    if not path.is_file():
        problems.append(f"{label} file is missing")
        return 0
    size, digest = _file_integrity(path)
    if not _nonnegative(body.get("captured_bytes")) or body["captured_bytes"] != size:
        problems.append(f"{label} length mismatch")
    if not isinstance(body.get("sha256"), str) or body["sha256"].lower() != digest:
        problems.append(f"{label} sha256 mismatch")
    if not _nonnegative(body.get("original_bytes")) or body.get("captured_bytes") != body.get("original_bytes"):
        problems.append(f"{label} capture is truncated")
    return size


def _validate_websocket(run: Path, flow: dict, manifest: dict, events: list[int],
                        problems: list[str]) -> tuple[int, int, int, int, int]:
    fid = flow["flow_id"]
    ws = flow.get("websocket")
    if not isinstance(ws, dict):
        raise CaptureError("WebSocket lifecycle is missing")
    if manifest.get("websocket_capture_scope") != "completed_application_messages":
        problems.append(f"{fid}: WebSocket capture scope is missing or unsupported")
    if flow.get("session_id") != manifest.get("session_id"):
        problems.append(f"{fid}: WebSocket session correlation mismatch")
    if ws.get("completeness") != "complete":
        problems.append(f"{fid}: WebSocket is incomplete")
    for key in ("observed_messages", "recorded_messages", "dropped_messages", "original_bytes", "captured_bytes"):
        if not _nonnegative(ws.get(key)):
            raise CaptureError(f"WebSocket {key} is invalid")
    if ws["dropped_messages"] or ws["observed_messages"] != ws["recorded_messages"]:
        problems.append(f"{fid}: WebSocket messages were dropped or not recorded")
    start, end = ws.get("start"), ws.get("end")
    for name, event in (("start", start), ("end", end)):
        if not isinstance(event, dict) or type(event.get("event_sequence")) is not int or event["event_sequence"] < 1:
            raise CaptureError(f"WebSocket {name} event is missing or invalid")
        events.append(event["event_sequence"])
        if not _iso_timestamp(event.get("timestamp")):
            problems.append(f"{fid}: WebSocket {name} timestamp is missing")
    if end["event_sequence"] <= start["event_sequence"]:
        problems.append(f"{fid}: WebSocket lifecycle order is invalid")
    request, response = flow.get("request"), flow.get("response")
    if (not isinstance(request, dict) or not request.get("client_connection_id")
            or not isinstance(response, dict) or not response.get("server_connection_id")
            or not flow.get("mitmproxy_flow_id")):
        problems.append(f"{fid}: WebSocket connection correlation is missing")
    if (not _timestamp(end.get("closed_at")) or type(end.get("closed_by_client")) is not bool
            or end.get("close_code") not in (1000, 1001) or end.get("error")
            or not isinstance(end.get("close_reason"), str)):
        problems.append(f"{fid}: WebSocket close/error state is abnormal or missing")
    paths = sorted((run / "websockets" / fid).glob("*.json"))
    if len(paths) != ws["recorded_messages"]:
        problems.append(f"{fid}: WebSocket message count does not match files")
    previous = start["event_sequence"]
    original = captured = 0
    for number, path in enumerate(paths, 1):
        try:
            msg = _json(path)
            if not isinstance(msg, dict):
                raise CaptureError("WebSocket message must be an object")
            if (path.name != f"{number:08d}.json" or msg.get("sequence") != number
                    or msg.get("flow_id") != fid or msg.get("session_id") != manifest.get("session_id")
                    or msg.get("schema_version") != 2):
                problems.append(f"{fid}/{path.stem}: WebSocket message order/correlation mismatch")
            event = msg.get("event_sequence")
            if type(event) is not int or not previous < event < end["event_sequence"]:
                raise CaptureError("WebSocket message event order is invalid")
            previous = event
            events.append(event)
            if not _timestamp(msg.get("received_at")) or not _iso_timestamp(msg.get("timestamp")):
                problems.append(f"{fid}/{path.stem}: WebSocket timestamp is invalid")
            if msg.get("direction") not in ("client_to_server", "server_to_client") or msg.get("type") not in ("text", "binary"):
                problems.append(f"{fid}/{path.stem}: WebSocket direction/type is invalid")
            if msg.get("dropped") is not False or msg.get("injected") is not False:
                problems.append(f"{fid}/{path.stem}: WebSocket message was dropped/injected")
            payload = msg.get("payload")
            size = _validate_payload(run, payload, f"{fid}/{path.stem}: WebSocket payload", problems)
            captured += size
            limits = manifest.get("limits")
            limit = limits.get("max_websocket_message_bytes") if isinstance(limits, dict) else None
            if (not _nonnegative(limit) or not _nonnegative(payload.get("captured_bytes"))
                    or payload["captured_bytes"] > limit):
                problems.append(f"{fid}/{path.stem}: WebSocket message byte limit is invalid or exceeded")
            if _nonnegative(payload.get("original_bytes")) and _nonnegative(payload.get("captured_bytes")):
                original += payload["original_bytes"]
        except (CaptureError, OSError) as exc:
            problems.append(f"{fid}/{path.stem}: {exc}")
    if original != ws["original_bytes"] or captured != ws["captured_bytes"]:
        problems.append(f"{fid}: WebSocket payload byte totals mismatch")
    return 1, ws["recorded_messages"], ws["observed_messages"], ws["dropped_messages"], captured


def validate_run(run: Path) -> tuple[int, list[str]]:
    problems: list[str] = []
    count = 0
    manifest_path = run / "manifest.json"
    if not manifest_path.is_file():
        return 0, ["manifest.json is missing"]
    try:
        manifest = _json(manifest_path)
    except CaptureError as exc:
        return 0, [str(exc)]
    if not isinstance(manifest, dict):
        return 0, ["manifest root must be an object"]
    schema = manifest.get("schema_version")
    if schema not in (1, 2):
        problems.append("unsupported schema_version")
    if schema == 2 and not isinstance(manifest.get("session_id"), str):
        problems.append("session_id is missing")
    if manifest.get("status") != "complete":
        problems.append("run is not complete")
    if manifest.get("recording_healthy") is not True:
        problems.append("recording is not healthy")
    errors = manifest.get("errors", manifest.get("recording_errors", []))
    if errors:
        problems.append("run records capture errors")
    flow_dir = run / "flows"
    if not flow_dir.is_dir():
        problems.append("flows directory is missing")
        return 0, problems
    flow_files = sorted(flow_dir.glob("*.json"))
    expected_count = manifest.get("flow_count")
    if not isinstance(expected_count, int) or expected_count != len(flow_files):
        problems.append("manifest flow_count does not match flow files")
    if not flow_files:
        problems.append("run contains no flows")
    ws_totals = [0, 0, 0, 0, 0]
    http_bytes = 0
    ws_events: list[int] = []
    ws_ids: set[str] = set()
    for flow_path in flow_files:
        count += 1
        try:
            flow = _json(flow_path)
            if not isinstance(flow, dict):
                raise CaptureError("flow root must be an object")
            if flow.get("schema_version") != schema:
                problems.append(f"{flow_path.stem}: unsupported flow schema_version")
            if flow.get("completeness") != "complete":
                problems.append(f"{flow_path.stem}: flow is {flow.get('completeness', 'pending')}")
            for side in ("request", "response"):
                if not isinstance(flow.get(side), dict):
                    problems.append(f"{flow_path.stem}: {side} metadata is missing")
            for side in ("request_body", "response_body"):
                body = flow.get(side)
                if body is None:
                    problems.append(f"{flow_path.stem}: {side} descriptor is missing")
                    continue
                if not isinstance(body, dict):
                    raise CaptureError(f"{side} must be an object")
                if body.get("complete") is not True:
                    problems.append(f"{flow_path.stem}: {side} is incomplete")
                relpath = _safe_file(run, body.get("file"))
                if not relpath.is_file():
                    problems.append(f"{flow_path.stem}: {side} file is missing")
                    continue
                size, digest = _file_integrity(relpath)
                http_bytes += size
                if not isinstance(body.get("captured_bytes"), int) or body["captured_bytes"] != size:
                    problems.append(f"{flow_path.stem}: {side} length mismatch")
                if not isinstance(body.get("sha256"), str) or body["sha256"].lower() != digest:
                    problems.append(f"{flow_path.stem}: {side} sha256 mismatch")
                if body.get("captured_bytes") != body.get("original_bytes"):
                    problems.append(f"{flow_path.stem}: {side} capture is truncated")
            if flow.get("error"):
                problems.append(f"{flow_path.stem}: flow records an error")
            response = flow.get("response")
            upgraded = isinstance(response, dict) and response.get("status_code") == 101 and _headers(flow, "response").get("upgrade", "").lower() == "websocket"
            if upgraded or flow.get("websocket_expected") or "websocket" in flow:
                if schema != 2:
                    raise CaptureError("legacy HTTP-only recording cannot validate WebSocket traffic")
                if flow.get("flow_id") != flow_path.stem:
                    raise CaptureError("WebSocket flow_id does not match filename")
                ws_ids.add(flow_path.stem)
                if not upgraded:
                    problems.append(f"{flow_path.stem}: WebSocket HTTP upgrade is missing")
                totals = _validate_websocket(run, flow, manifest, ws_events, problems)
                ws_totals = [a + b for a, b in zip(ws_totals, totals)]
        except (CaptureError, OSError) as exc:
            problems.append(f"{flow_path.stem}: {exc}")
    if schema == 2:
        total_bytes = http_bytes + ws_totals[4]
        if not _nonnegative(manifest.get("captured_body_bytes")) or manifest["captured_body_bytes"] != total_bytes:
            problems.append("manifest captured_body_bytes does not match HTTP/WebSocket files")
        for key, total in zip(("websocket_count", "websocket_message_count", "websocket_observed_messages", "websocket_dropped_messages"), ws_totals[:4]):
            if type(manifest.get(key)) is not int or manifest[key] != total:
                problems.append(f"manifest {key} does not match WebSocket records")
        if sorted(ws_events) != list(range(1, len(ws_events) + 1)):
            problems.append("WebSocket event sequence has gaps or duplicates")
        orphaned = [p for p in (run / "websockets").glob("*/*.json") if p.parent.name not in ws_ids]
        if orphaned:
            problems.append("orphaned WebSocket message records")
        limits = manifest.get("limits", {})
        if not isinstance(limits, dict):
            problems.append("capture limits are invalid")
        else:
            for key, value in (("max_websocket_messages", ws_totals[1]), ("max_total_bytes", total_bytes)):
                if not _nonnegative(limits.get(key)) or not _nonnegative(value) or value > limits[key]:
                    problems.append(f"capture limit {key} is invalid or exceeded")
    return count, problems


def _headers(flow: dict[str, Any], side: str) -> dict[str, str]:
    section = flow.get(side)
    headers = section.get("headers", {}) if isinstance(section, dict) else {}
    if isinstance(headers, dict):
        return {str(k).lower(): str(v) for k, v in headers.items()}
    if isinstance(headers, list):
        return {str(x[0]).lower(): str(x[1]) for x in headers if isinstance(x, (list, tuple)) and len(x) == 2}
    return {}


def _inflate(data: bytes, encoding: str) -> bytes:
    enc = encoding.strip().lower()
    if not enc or enc == "identity":
        return data
    if enc == "gzip":
        dec = zlib.decompressobj(16 + zlib.MAX_WBITS)
    elif enc == "deflate":
        dec = zlib.decompressobj()
    else:
        raise CaptureError(f"unsupported content-encoding {enc}")
    try:
        out = dec.decompress(data, MAX_DECOMPRESSED_BYTES + 1)
        if len(out) > MAX_DECOMPRESSED_BYTES or dec.unconsumed_tail:
            raise CaptureError("decompressed body exceeds 32 MiB limit")
        out += dec.flush(MAX_DECOMPRESSED_BYTES + 1 - len(out))
    except zlib.error as exc:
        raise CaptureError("compressed body is invalid") from exc
    if len(out) > MAX_DECOMPRESSED_BYTES:
        raise CaptureError("decompressed body exceeds 32 MiB limit")
    if not dec.eof:
        raise CaptureError("compressed body is truncated")
    if dec.unused_data:
        raise CaptureError("trailing data or multiple compressed members are unsupported")
    return out


def _load_body(run: Path, flow: dict[str, Any], side: str) -> bytes | None:
    body = flow.get(side + "_body")
    if not isinstance(body, dict) or not body.get("file"):
        return None
    return _safe_file(run, body["file"]).read_bytes()


def _content_id(value: str | None) -> str:
    if value is None:
        return ""
    return value.strip().strip("<>").strip().lower()


def extract_flow(run: Path, flow: dict[str, Any], side: str, out: Path) -> int:
    raw = _load_body(run, flow, side)
    if raw is None:
        return 0
    headers = _headers(flow, side)
    raw = _inflate(raw, headers.get("content-encoding", ""))
    ctype = headers.get("content-type", "application/octet-stream")
    wrapped = (f"Content-Type: {ctype}".encode("ascii", "replace") + b"\r\nMIME-Version: 1.0\r\n\r\n" + raw)
    msg = BytesParser(policy=policy.default).parsebytes(wrapped)
    if msg.get_content_maintype() == "multipart" and not msg.is_multipart():
        raise CaptureError(f"malformed multipart {side}")
    if not msg.is_multipart():
        ext = ".xml" if "xml" in ctype.lower() or raw.lstrip().startswith(b"<") else ".bin"
        (out / f"{side}{ext}").write_bytes(raw)
        return 1

    parts: list[tuple[bytes, str, str]] = []
    cid_map: dict[str, bytes] = {}
    for i, part in enumerate(msg.walk()):
        if part.is_multipart():
            continue
        payload = part.get_payload(decode=True)
        if payload is None:
            payload = b""
        cid = _content_id(part.get("Content-ID"))
        parts.append((payload, cid, part.get_content_type().lower()))
        if cid:
            if cid in cid_map:
                raise CaptureError(f"duplicate MIME content-id in {side}")
            cid_map[cid] = payload
    if not parts:
        raise CaptureError(f"multipart {side} contains no parts")
    if any(part.defects for part in msg.walk()):
        raise CaptureError(f"malformed multipart {side}")
    start = _content_id(msg.get_param("start"))
    root_index = next((i for i, (_, cid, _) in enumerate(parts) if cid == start), 0) if start else 0
    if start and not any(cid == start for _, cid, _ in parts):
        raise CaptureError(f"multipart root content-id is missing in {side}")
    root_payload, _, root_type = parts[root_index]
    if "xml" not in root_type and not root_payload.lstrip().startswith(b"<"):
        raise CaptureError(f"multipart root is not XML in {side}")
    try:
        root = ET.fromstring(root_payload)
    except ET.ParseError as exc:
        raise CaptureError(f"multipart root XML is malformed in {side}") from exc
    refs: set[str] = set()
    for node in root.iter():
        if node.tag == "{http://www.w3.org/2004/08/xop/include}Include":
            href = node.attrib.get("href", "")
            if href.lower().startswith("cid:"):
                refs.add(_content_id(unquote(href[4:])))
    missing = refs.difference(cid_map)
    if missing:
        raise CaptureError(f"unresolved xop content-id reference in {side}")
    for i, (payload, _, content_type) in enumerate(parts):
        suffix = ".xml" if "xml" in content_type or payload.lstrip().startswith(b"<") else ".bin"
        (out / f"{side}-{i:03d}{suffix}").write_bytes(payload)
    return len(parts)


def inspect_run(run: Path, output: Path) -> tuple[int, list[str]]:
    count, problems = validate_run(run)
    if problems:
        return count, problems
    if output.exists():
        return count, ["output directory already exists"]
    run_resolved = run.resolve()
    output_resolved = output.resolve()
    if output_resolved == run_resolved or run_resolved in output_resolved.parents:
        return count, ["output directory must be outside the capture run"]
    try:
        output.mkdir(parents=True, exist_ok=False)
    except OSError as exc:
        return count, [f"cannot create output directory: {exc}"]
    created_output = output.resolve()
    if created_output != output_resolved:
        return count, ["output directory changed while being created"]
    extracted = 0
    try:
        for flow_file in sorted((run / "flows").glob("*.json")):
            flow = _json(flow_file)
            target = output / flow_file.stem
            target.mkdir()
            for side in ("request", "response"):
                extracted += extract_flow(run, flow, side, target)
            if "websocket" in flow:
                ws_target = target / "websocket"
                ws_target.mkdir()
                for path in sorted((run / "websockets" / flow_file.stem).glob("*.json")):
                    msg = _json(path)
                    suffix = ".txt" if msg["type"] == "text" else ".bin"
                    shutil.copyfile(_safe_file(run, msg["payload"]["file"]), ws_target / (path.stem + suffix))
                    shutil.copyfile(path, ws_target / path.name)
                    extracted += 1
    except (CaptureError, OSError) as exc:
        # Do not leave a partial export that could be mistaken for a complete one.
        # Remove only the exact directory this call created, after checking it is
        # still outside the input run and remains the same resolved path.
        if output.exists() and output.resolve() == created_output and run_resolved not in created_output.parents:
            shutil.rmtree(created_output, ignore_errors=True)
        return count, [str(exc)]
    return extracted, []


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subs = parser.add_subparsers(dest="command", required=True)
    val = subs.add_parser("validate", help="check run completeness and body integrity")
    val.add_argument("run", type=Path)
    ins = subs.add_parser("inspect", aliases=["extract"], help="extract HTTP bodies and MIME parts for local review")
    ins.add_argument("run", type=Path)
    ins.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    if args.command == "validate":
        count, problems = validate_run(args.run)
        print(f"flows={count} status={'valid' if not problems else 'invalid'}")
    else:
        count, problems = inspect_run(args.run, args.output)
        print(f"extracted_parts={count} status={'complete' if not problems else 'failed'}")
    for problem in problems:
        print(f"error: {problem}", file=sys.stderr)
    if args.command in ("inspect", "extract"):
        print("Extracted payloads may contain document content, usernames, and URLs. Review locally before sharing.", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
