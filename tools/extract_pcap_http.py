"""Reassemble TCP payloads from a pcap/pcapng file and export matching flows."""

from __future__ import annotations

import argparse
import ipaddress
from collections import defaultdict
from pathlib import Path

import dpkt


def endpoint(address: bytes, port: int) -> tuple[str, int]:
    return str(ipaddress.ip_address(address)), port


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("capture", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--contains", default="cellstorage")
    args = parser.parse_args()

    directions: dict[tuple[tuple[str, int], tuple[str, int]], dict[int, int]] = defaultdict(dict)
    with args.capture.open("rb") as source:
        try:
            reader = dpkt.pcapng.Reader(source)
        except ValueError:
            source.seek(0)
            reader = dpkt.pcap.Reader(source)

        for _, packet in reader:
            try:
                ethernet = dpkt.ethernet.Ethernet(packet)
                ip = ethernet.data
                tcp = ip.data
                if not isinstance(tcp, dpkt.tcp.TCP) or not tcp.data:
                    continue
                key = (endpoint(ip.src, tcp.sport), endpoint(ip.dst, tcp.dport))
                payload = directions[key]
                for offset, value in enumerate(tcp.data):
                    payload.setdefault(tcp.seq + offset, value)
            except (AttributeError, dpkt.UnpackError):
                continue

    streams: dict[tuple[tuple[str, int], tuple[str, int]], bytes] = {}
    for key, values in directions.items():
        if not values:
            continue
        positions = sorted(values)
        chunks: list[bytearray] = []
        previous = None
        for position in positions:
            if previous is None or position != previous + 1:
                chunks.append(bytearray())
            chunks[-1].append(values[position])
            previous = position
        streams[key] = b"\n\n--- TCP GAP ---\n\n".join(bytes(chunk) for chunk in chunks)

    needle = args.contains.encode("ascii").lower()
    connections: dict[tuple[tuple[str, int], tuple[str, int]], dict] = {}
    for (source, destination), data in streams.items():
        connection = tuple(sorted((source, destination)))
        entry = connections.setdefault(connection, {})
        entry[(source, destination)] = data

    args.output.mkdir(parents=True, exist_ok=True)
    exported = 0
    for connection, halves in connections.items():
        if not any(needle in data.lower() for data in halves.values()):
            continue
        exported += 1
        for index, ((source, destination), data) in enumerate(halves.items(), start=1):
            name = f"flow-{exported:02d}-direction-{index}-{source[0]}-{source[1]}-to-{destination[0]}-{destination[1]}.bin"
            (args.output / name).write_bytes(data)
            print(f"{name}: {len(data)} bytes")

    print(f"Exported {exported} matching TCP connection(s) to {args.output}")


if __name__ == "__main__":
    main()
