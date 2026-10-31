"""Publish validated artifacts with a temporary key, stopping at the first failure."""
import argparse
import io
from urllib.error import HTTPError
from urllib.request import urlopen
import zipfile
import os
from pathlib import Path
import subprocess
from package_manifest import PROJECTS, validate_release


def public_package(package_id, version):
    name = package_id.lower()
    url = f"https://api.nuget.org/v3-flatcontainer/{name}/{version.lower()}/{name}.{version.lower()}.nupkg"
    try:
        with urlopen(url, timeout=60) as response:
            return response.read()
    except HTTPError as error:
        if error.code == 404:
            return None
        raise


def package_payload(data):
    # Repository signing changes the ZIP container and adds only this signature.
    # Compare every other entry byte-for-byte, including nuspec provenance.
    with zipfile.ZipFile(io.BytesIO(data)) as package:
        names = package.namelist()
        if len(names) != len(set(names)):
            raise RuntimeError("Duplicate ZIP entries in a release package.")
        return {name: package.read(name) for name in names if name != ".signature.p7s"}


def resume_primaries(output, version, fetch=public_package):
    identical = set()
    for name in PROJECTS:
        public = fetch(name, version)
        if public is None:
            continue
        retained = Path(output) / f"{name}.{version}.nupkg"
        if package_payload(public) != package_payload(retained.read_bytes()):
            raise RuntimeError(f"Public package conflicts with retained artifact: {retained.name}")
        identical.add(name)
        print(f"Verified identical public payload: {retained.name}")
    return identical


def push_packages(output, version, api_key, run=subprocess.run, *, resume=False, fetch=public_package):
    identical = resume_primaries(output, version, fetch) if resume else set()
    for extension in ("nupkg", "snupkg"):
        for name in PROJECTS:
            if extension == "nupkg" and name in identical:
                continue
            package = Path(output) / f"{name}.{version}.{extension}"
            command = ["dotnet", "nuget", "push", str(package), "--api-key", api_key,
                       "--source", "https://api.nuget.org/v3/index.json"]
            # Suppress automatic symbol uploads while publishing the primaries.
            # Explicit snupkg uploads need symbol-endpoint discovery enabled.
            if extension == "nupkg":
                command.append("--no-symbols")
            # NuGet does not expose snupkg archives for download. In resume mode
            # tolerate a symbol-endpoint 409 only after every public primary has
            # passed payload comparison. Other symbol failures still stop the run.
            if resume and extension == "snupkg":
                command.append("--skip-duplicate")
            if run(command).returncode:
                # Do not include the command or key in an exception traceback.
                raise SystemExit(f"Publishing stopped at {package.name}; inspect the NuGet error above.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=Path("artifacts/packages"))
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--resume", action="store_true")
    mode.add_argument("--preflight-resume", action="store_true")
    args = parser.parse_args()
    validate_release(args.directory, args.version, args.commit)
    if args.preflight_resume:
        resume_primaries(args.directory, args.version)
        return
    api_key = os.environ.get("NUGET_API_KEY")
    if not api_key:
        parser.error("NUGET_API_KEY must contain the temporary publishing credential.")
    push_packages(args.directory, args.version, api_key, resume=args.resume)


if __name__ == "__main__":
    main()
