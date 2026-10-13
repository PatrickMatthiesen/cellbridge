"""Create private capture output without changing existing directory permissions."""
from __future__ import annotations

import os
from pathlib import Path


def private_directory(path: Path, *, exist_ok: bool = True) -> None:
    # Path.mkdir(parents=True) applies its mode only to the last directory.
    # Create each missing parent with the same restrictive permissions.
    try:
        path.mkdir(mode=0o700, exist_ok=exist_ok)
    except FileNotFoundError:
        private_directory(path.parent)
        path.mkdir(mode=0o700, exist_ok=exist_ok)


def private_open(path: Path, mode: str = "wb", **kwargs):
    # Exclusive creation rejects pre-existing files and symlinks. Permissions
    # apply at creation, before the caller can write any sensitive bytes.
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_BINARY", 0)
    descriptor = os.open(path, flags, 0o600)
    try:
        return os.fdopen(descriptor, mode, **kwargs)
    except Exception:
        os.close(descriptor)
        raise


def private_write(path: Path, content: bytes) -> None:
    with private_open(path) as stream:
        stream.write(content)
