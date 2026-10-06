#!/usr/bin/env python3
"""Verify repeated Office content, per-save capture and separate manual UI evidence."""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET
import zipfile


def utc(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None or parsed.utcoffset().total_seconds() != 0:
        raise ValueError("Evidence timestamps must include UTC timezone")
    return parsed


def identity(url):
    parsed = urlsplit(url)
    return parsed.scheme.lower(), parsed.netloc.lower(), unquote(parsed.path).casefold()


def artifact(root, name):
    if not isinstance(name, str) or not name:
        raise ValueError("Missing artifact path")
    path = (root / name).resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        raise ValueError("Artifact must exist inside evidence directory")
    return path


def successful_capture(record, url):
    if record.get("schemaVersion") != 1:
        return False
    for file in record.get("files", []):
        if identity(file["url"]) != identity(url) or file.get("errorCode") not in (None, "Success"):
            continue
        for sub in file.get("subResponses", []):
            binary = sub.get("binary")
            if (sub.get("partition") == "FileContents" and sub.get("errorCode") in (None, "Success")
                    and binary and binary.get("failed") is False
                    and any(op.get("type") == "PutChanges" and op.get("failed") is False
                            for op in binary.get("operations", []))):
                return True
    return False


def package_content(path):
    with zipfile.ZipFile(path) as package:
        text = []
        images = 0
        for name in package.namelist():
            if name.startswith("word/media/") and not name.endswith("/"):
                images += 1
            if name in ("word/document.xml", "xl/sharedStrings.xml") or (name.startswith("xl/worksheets/") and name.endswith(".xml")):
                root = ET.fromstring(package.read(name))
                text.append("".join(node.text or "" for node in root.iter() if node.tag.rsplit("}", 1)[-1] == "t"))
        return "\n".join(text), images


def verify(result_path, capture_directory, manual_path=None, minimum_cycles=None):
    """Fail closed on missing/malformed evidence; do not infer client behavior from HTTP success."""
    errors = []
    matches = {}
    try:
        result = json.loads(result_path.read_text(encoding="utf-8-sig"))
        application = result["application"]
        if result["schemaVersion"] != 1 or application not in ("Word", "Excel"):
            raise ValueError("Unsupported evidence schema/application")
        if not re.fullmatch(r"[a-fA-F0-9]{40}", result["candidateCommit"]) or not re.fullmatch(r"[a-fA-F0-9]{64}", result["scriptSha256"]):
            raise ValueError("Missing candidate commit or script hash")
        if not result.get("officeVersion") or not result.get("officeBuild"):
            raise ValueError("Missing Office version/build")
        for key in ("workerFinished", "contentCheckPassed"):
            if result.get(key) is not True:
                raise ValueError(f"{key} was not successful")
        if result.get("error") or result.get("cleanupError"):
            raise ValueError("Office run reports an error")
        required = minimum_cycles if minimum_cycles is not None else (20 if application == "Word" else 10)
        count = result["expectedCycles"]
        cycles = result["cycles"]
        if type(count) is not int or count < required or len(cycles) != count:
            raise ValueError("Missing required cycles")
        if identity(result["origin"])[:2] != identity(result["documentUrl"])[:2]:
            raise ValueError("Document origin differs from tested origin")
        captures = [(path.name, json.loads(path.read_text(encoding="utf-8-sig")))
                    for path in capture_directory.glob("*.summary.json")]
        previous_etag = None
        previous_hash = None
        previous_end = utc(result["startedUtc"])
        markers = []
        used = set()
        for index, cycle in enumerate(cycles, 1):
            if cycle["cycle"] != index or type(cycle["saveCalls"]) is not int or cycle["saveCalls"] != 1:
                raise ValueError(f"Cycle {index} identity/save count invalid")
            if any(cycle.get(key) is not True for key in ("saved", "reopened", "remoteContentVerified")):
                raise ValueError(f"Cycle {index} missing Saved/content/reopen evidence")
            start, returned, end, reopened = (utc(cycle[key]) for key in
                ("saveStartedUtc", "saveReturnedUtc", "saveVerifiedUtc", "reopenedUtc"))
            if not previous_end <= start <= returned <= end <= reopened <= utc(result["completedUtc"]):
                raise ValueError(f"Cycle {index} timestamps out of order")
            previous_end = reopened
            if not cycle["etag"] or cycle["etag"] == previous_etag:
                raise ValueError(f"Cycle {index} missing changed ETag")
            previous_etag = cycle["etag"]
            path = artifact(result_path.parent, cycle["packageFile"])
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            if digest != cycle["sha256"].lower() or digest == previous_hash:
                raise ValueError(f"Cycle {index} package hash mismatch or unchanged bytes")
            previous_hash = digest
            text, images = package_content(path)
            marker = cycle["marker"]
            if not isinstance(marker, str) or not marker or marker in markers:
                raise ValueError(f"Cycle {index} missing unique edit marker")
            markers.append(marker)
            if any(marker not in text for marker in markers):
                raise ValueError(f"Cycle {index} downloaded package lost edits")
            expected_images = index // 2 if application == "Word" else 0
            expected_kind = "image" if application == "Word" and index % 2 == 0 else "text"
            if (cycle["editKind"] != expected_kind or cycle["expectedImages"] != expected_images
                    or images != cycle["packageImages"] or images < expected_images):
                raise ValueError(f"Cycle {index} missing image edit evidence")
            found = [name for name, record in captures if name not in used
                     and successful_capture(record, result["documentUrl"])
                     and start <= utc(record["completedUtc"]) <= end]
            if not found:
                raise ValueError(f"Cycle {index} has no correlated successful file PutChanges")
            used.update(found)
            matches[str(index)] = found
        automated = True
    except (ValueError, TypeError, KeyError, AttributeError, OSError, zipfile.BadZipFile, ET.ParseError) as error:
        errors.append(str(error))
        automated = False
    manual = False
    if automated:
        try:
            if manual_path is None:
                raise ValueError("Manual Saved UI and normal-close prompt qualification is still required")
            observation = json.loads(manual_path.read_text(encoding="utf-8-sig"))
            for key in ("candidateCommit", "application", "origin", "officeBuild"):
                if observation[key] != result[key]:
                    raise ValueError(f"Manual evidence {key} does not match")
            if not observation.get("observer") or len(observation["cycles"]) < required:
                raise ValueError("Missing manual observer/cycles")
            for index, cycle in enumerate(observation["cycles"], 1):
                if cycle["cycle"] != index or any(cycle.get(key) is not True for key in
                        ("singleSave", "savedUi", "normalCloseWithoutPrompt", "reopenedContent")):
                    raise ValueError("Manual single-save/Saved/normal-close/reopen gate failed")
                if application == "Word" and index % 2 == 0 and cycle.get("imageEdit") is not True:
                    raise ValueError("Missing manual image-edit cycle")
                if utc(cycle["observedUtc"]) < utc(result["startedUtc"]):
                    raise ValueError("Manual observation predates this candidate run")
                evidence = artifact(manual_path.parent, cycle["evidenceFile"])
                if hashlib.sha256(evidence.read_bytes()).hexdigest() != cycle["sha256"].lower():
                    raise ValueError("Manual evidence hash mismatch")
            manual = True
        except (ValueError, TypeError, KeyError, AttributeError, OSError) as error:
            errors.append(str(error))
    return {"passed": automated and manual, "automatedContentPassed": automated,
            "manualUiPassed": manual, "acceptedPutChangesByCycle": matches, "errors": errors,
            "scope": "One Office desktop; does not establish coauthoring or eliminate intermittent failures."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--result", required=True, type=Path)
    parser.add_argument("--capture", required=True, type=Path)
    parser.add_argument("--manual", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = verify(args.result, args.capture, args.manual)
    rendered = json.dumps(report, indent=2)
    print(rendered)
    if args.output:
        args.output.write_text(rendered + "\n", encoding="utf-8")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
