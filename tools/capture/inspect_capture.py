#!/usr/bin/env python3
"""Validate and inspect an CellBridge capture run (stdlib only)."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zlib
import shutil
import xml.etree.ElementTree as ET
from urllib.parse import unquote
from email import policy
from email.parser import BytesParser
from pathlib import Path, PurePosixPath
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
    if manifest.get("schema_version") != 1:
        problems.append("unsupported schema_version")
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
    for flow_path in flow_files:
        count += 1
        try:
            flow = _json(flow_path)
            if not isinstance(flow, dict):
                raise CaptureError("flow root must be an object")
            if flow.get("schema_version") != 1:
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
                if not isinstance(body.get("captured_bytes"), int) or body["captured_bytes"] != size:
                    problems.append(f"{flow_path.stem}: {side} length mismatch")
                if not isinstance(body.get("sha256"), str) or body["sha256"].lower() != digest:
                    problems.append(f"{flow_path.stem}: {side} sha256 mismatch")
                if body.get("captured_bytes") != body.get("original_bytes"):
                    problems.append(f"{flow_path.stem}: {side} capture is truncated")
            if flow.get("error"):
                problems.append(f"{flow_path.stem}: flow records an error")
        except (CaptureError, OSError) as exc:
            problems.append(f"{flow_path.stem}: {exc}")
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
