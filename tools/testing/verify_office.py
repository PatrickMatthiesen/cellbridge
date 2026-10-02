#!/usr/bin/env python3
"""Require remote content verification and a successful captured file PutChanges."""
import argparse
import json
from pathlib import Path
from urllib.parse import unquote, urlsplit


def accepted_saves(capture, document_url):
    expected = unquote(urlsplit(document_url).path).casefold()
    accepted = []
    for path in Path(capture).glob("*.summary.json"):
        record = json.loads(path.read_text())
        if record.get("schemaVersion") != 1:
            continue
        for file in record.get("files", []):
            if unquote(urlsplit(file["url"]).path).casefold() != expected:
                continue
            if file.get("errorCode") not in (None, "Success"):
                continue
            for sub in file.get("subResponses", []):
                binary = sub.get("binary")
                if sub.get("partition") != "FileContents" or sub.get("errorCode") not in (None, "Success"):
                    continue
                if binary and not binary.get("failed", True):
                    if any(op.get("type") == "PutChanges" and op.get("failed") is False
                           for op in binary.get("operations", [])):
                        accepted.append(path.name)
    return sorted(set(accepted))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", required=True, type=Path)
    parser.add_argument("--word-result", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    word = json.loads(args.word_result.read_text(encoding="utf-8-sig"))
    saves = accepted_saves(args.capture, word["documentUrl"])
    passed = (word.get("contentCheckPassed") is True and word.get("workerFinished") is True
              and word.get("remoteContentVerified") is True and word.get("reopenedInWord") is True and bool(saves))
    report = {"passed": passed, "documentUrl": word["documentUrl"], "acceptedPutChangesCaptures": saves,
              "wordResult": word, "scope": "One desktop Word client. Does not verify two-client coauthoring."}
    rendered = json.dumps(report, indent=2)
    print(rendered)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered + "\n")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
