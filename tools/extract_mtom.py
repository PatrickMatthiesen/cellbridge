"""Extract MIME parts from an MTOM HTTP response body."""

from __future__ import annotations

import argparse
from email import policy
from email.parser import BytesParser
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("body", type=Path)
    parser.add_argument("boundary")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    envelope = (
        f'Content-Type: multipart/related; boundary="{args.boundary}"\r\n'
        "MIME-Version: 1.0\r\n\r\n"
    ).encode("ascii") + args.body.read_bytes()
    message = BytesParser(policy=policy.default).parsebytes(envelope)
    if not message.is_multipart():
        raise SystemExit("Input was not parsed as multipart MIME")

    args.output.mkdir(parents=True, exist_ok=True)
    for index, part in enumerate(message.iter_parts()):
        payload = part.get_payload(decode=True) or b""
        suffix = ".xml" if part.get_content_type() in {"text/xml", "application/xop+xml"} else ".bin"
        target = args.output / f"part-{index:02d}{suffix}"
        target.write_bytes(payload)
        print(f"{target.name}: type={part.get_content_type()} id={part.get('Content-ID')} bytes={len(payload)}")


if __name__ == "__main__":
    main()
