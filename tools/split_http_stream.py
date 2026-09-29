"""Split a reassembled HTTP/1.x byte stream into header and body files."""

from __future__ import annotations

import argparse
import re
from pathlib import Path


START = re.compile(rb"(?m)^(?:HTTP/1\.[01] \d{3}|(?:OPTIONS|GET|HEAD|POST|PUT|DELETE|PATCH) \S+ HTTP/1\.[01])")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("stream", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    data = args.stream.read_bytes()
    starts = [match.start() for match in START.finditer(data)]
    args.output.mkdir(parents=True, exist_ok=True)

    for number, start in enumerate(starts, start=1):
        header_end = data.find(b"\r\n\r\n", start)
        if header_end < 0:
            continue
        header = data[start:header_end]
        next_start = starts[number] if number < len(starts) else len(data)
        length_match = re.search(rb"(?mi)^Content-Length:\s*(\d+)\s*$", header)
        body_start = header_end + 4
        body_end = min(next_start, body_start + int(length_match.group(1))) if length_match else next_start
        body = data[body_start:body_end]
        prefix = args.output / f"message-{number:02d}"
        prefix.with_suffix(".headers.txt").write_bytes(header + b"\r\n")
        prefix.with_suffix(".body.bin").write_bytes(body)
        first_line = header.split(b"\r\n", 1)[0].decode("latin1", "replace")
        print(f"{number:02d} {first_line} body={len(body)}")


if __name__ == "__main__":
    main()
