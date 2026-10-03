"""Provision the disposable runner's ordinary Identity account before imports."""
import os
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
configuration = os.environ.get("CELLBRIDGE_TEST_CONFIGURATION", "Release")
dll = root / f"tools/CellBridge.Admin/bin/{configuration}/net10.0/CellBridge.Admin.dll"
subprocess.run(["dotnet", str(dll), "create-user", "--id", "integration-writer",
                "--login", "integration-writer", "--display-name", "Integration writer",
                "--can-create", "true"], input=os.environ["CELLBRIDGE_TEST_PASSWORD"] + "\n",
               text=True, check=True, cwd=root)
