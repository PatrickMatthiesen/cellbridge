"""Ask a capture run to flush its manifest and stop, including under Aspire."""
import argparse
import json
from pathlib import Path
import time


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    args = parser.parse_args()
    manifest = args.run / "manifest.json"
    if not manifest.is_file():
        parser.error("run has no manifest.json")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    if data.get("status") != "running":
        print(f"Run already stopped: {data.get('status', 'unknown')}")
        return 0
    (args.run / "stop.request").touch()
    for _ in range(40):
        time.sleep(0.25)
        data = json.loads(manifest.read_text(encoding="utf-8"))
        if data.get("status") != "running":
            print(f"Capture stopped: {data['status']}")
            return 0
    print("No shutdown acknowledgement. The process may have exited without finalizing; validate the run.")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
