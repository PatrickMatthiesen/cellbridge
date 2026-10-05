"""Check tested artifacts before exchanging a publishing token or uploading packages."""
import argparse
import pathlib
import xml.etree.ElementTree as ET
from package_manifest import validate_release


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=pathlib.Path, default=pathlib.Path("artifacts/packages"))
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
    if args.version != version:
        parser.error("Requested release version must match the version committed in Directory.Build.props.")
    manifest = validate_release(args.directory, args.version, args.commit)
    print(f"Validated {len(manifest['packages'])} package/symbol pairs at {args.version} from {args.commit}.")


if __name__ == "__main__":
    main()
