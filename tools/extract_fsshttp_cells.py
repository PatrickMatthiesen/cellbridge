"""Extract base64 and XOP-backed FSSHTTPB SubResponseData payloads from SOAP."""

from __future__ import annotations

import argparse
import base64
import xml.etree.ElementTree as ET
from pathlib import Path


SOAP_NAMESPACE = "{http://schemas.microsoft.com/sharepoint/soap/}"
XOP_INCLUDE = "{http://www.w3.org/2004/08/xop/include}Include"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("soap", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--xop", type=Path)
    args = parser.parse_args()

    root = ET.fromstring(args.soap.read_bytes())
    args.output.mkdir(parents=True, exist_ok=True)
    for response in root.findall(f".//{SOAP_NAMESPACE}SubResponse"):
        token = response.get("SubRequestToken", "unknown")
        data = response.find(f"{SOAP_NAMESPACE}SubResponseData")
        if data is None:
            continue
        include = data.find(XOP_INCLUDE)
        if include is not None:
            if args.xop is None:
                print(f"token {token}: XOP payload not supplied")
                continue
            payload = args.xop.read_bytes()
        else:
            text = (data.text or "").strip()
            if not text:
                continue
            try:
                payload = base64.b64decode(text, validate=True)
            except ValueError:
                continue
        target = args.output / f"token-{token}.fsshttpb"
        target.write_bytes(payload)
        print(f"token {token}: {len(payload)} bytes -> {target.name}")


if __name__ == "__main__":
    main()
