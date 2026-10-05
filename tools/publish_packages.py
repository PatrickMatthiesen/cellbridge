"""Publish validated artifacts with a temporary key, stopping at the first failure."""
import argparse
import os
from pathlib import Path
import subprocess
from package_manifest import PROJECTS, validate_release


def push_packages(output, version, api_key, run=subprocess.run):
    for extension in ("nupkg", "snupkg"):
        for name in PROJECTS:
            package = Path(output) / f"CellBridge.{name}.{version}.{extension}"
            command = ["dotnet", "nuget", "push", str(package), "--api-key", api_key,
                       "--source", "https://api.nuget.org/v3/index.json"]
            # Suppress automatic symbol uploads while publishing the primaries.
            # Explicit snupkg uploads need symbol-endpoint discovery enabled.
            if extension == "nupkg":
                command.append("--no-symbols")
            if run(command).returncode:
                # Do not include the command or key in an exception traceback.
                raise SystemExit(f"Publishing stopped at {package.name}; inspect the NuGet error above.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=Path("artifacts/packages"))
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    validate_release(args.directory, args.version, args.commit)
    api_key = os.environ.get("NUGET_API_KEY")
    if not api_key:
        parser.error("NUGET_API_KEY must contain the temporary publishing credential.")
    push_packages(args.directory, args.version, api_key)


if __name__ == "__main__":
    main()
