import hashlib
import json
import tempfile
import unittest
import gzip
from pathlib import Path

from inspect_capture import CaptureError, extract_flow, validate_run, inspect_run


class InspectCaptureTests(unittest.TestCase):
    def make_run(self, root: Path, body: bytes = b'<soap/>') -> Path:
        run = root / "run"
        (run / "flows").mkdir(parents=True)
        (run / "files").mkdir()
        data_path = run / "files" / "a.request.body.bin"
        data_path.write_bytes(body)
        response_path = run / "files" / "a.response.body.bin"
        response_path.write_bytes(b"<response/>")
        body_meta = {"file": "files/a.request.body.bin", "sha256": hashlib.sha256(body).hexdigest(),
                     "original_bytes": len(body), "captured_bytes": len(body), "complete": True}
        response_meta = {"file": "files/a.response.body.bin", "sha256": hashlib.sha256(b"<response/>").hexdigest(),
                         "original_bytes": 11, "captured_bytes": 11, "complete": True}
        flow = {"schema_version": 1, "completeness": "complete",
                "request": {"headers": {"content-type": "text/xml"}},
                "response": {"headers": {"content-type": "text/xml"}},
                "request_body": body_meta, "response_body": response_meta}
        (run / "flows" / "a.json").write_text(json.dumps(flow), encoding="utf-8")
        manifest = {"schema_version": 1, "status": "complete", "recording_healthy": True, "flow_count": 1,
                    "errors": [], "hosts": [], "timestamps": {}}
        (run / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        return run

    def test_valid_run_and_hash_mismatch(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp))
            count, errors = validate_run(run)
            self.assertEqual(count, 1)
            self.assertEqual(errors, [])
            (run / "files" / "a.request.body.bin").write_bytes(b"changed")
            _, errors = validate_run(run)
            self.assertTrue(any("sha256 mismatch" in e for e in errors))

    def test_multipart_without_boundary_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp), b"incomplete MIME data")
            flow = json.loads((run / "flows/a.json").read_text())
            flow["request"]["headers"] = {"content-type": "multipart/related; boundary=missing"}
            out = Path(tmp) / "out"
            out.mkdir()
            with self.assertRaisesRegex(CaptureError, "malformed multipart"):
                extract_flow(run, flow, "request", out)

    def test_requires_flow_sides_descriptors_and_matching_manifest_count(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp))
            flow_path = run / "flows" / "a.json"
            flow = json.loads(flow_path.read_text())
            flow.pop("request_body")
            flow.pop("response")
            flow["schema_version"] = 2
            flow_path.write_text(json.dumps(flow))
            _, errors = validate_run(run)
            self.assertTrue(any("request_body descriptor is missing" in e for e in errors))
            self.assertTrue(any("response metadata is missing" in e for e in errors))
            self.assertTrue(any("flow schema_version" in e for e in errors))
            (run / "flows" / "lost.json").write_text("{}")
            _, errors = validate_run(run)
            self.assertTrue(any("flow_count" in e for e in errors))

    def test_rejects_path_traversal(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp))
            flow_path = run / "flows" / "a.json"
            flow = json.loads(flow_path.read_text())
            flow["request_body"]["file"] = "../outside.bin"
            flow_path.write_text(json.dumps(flow))
            _, errors = validate_run(run)
            self.assertTrue(any("unsafe" in e for e in errors))

    def test_detects_pending_incomplete_and_missing_manifest(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp))
            manifest_path = run / "manifest.json"
            manifest = json.loads(manifest_path.read_text())
            manifest["status"] = "running"
            manifest_path.write_text(json.dumps(manifest))
            _, errors = validate_run(run)
            self.assertTrue(any("not complete" in e for e in errors))
            manifest_path.unlink()
            _, errors = validate_run(run)
            self.assertIn("manifest.json is missing", errors)

    def test_extracts_multipart_and_preserves_binary_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp), b"ignored")
            payload = (b'--xyz\r\nContent-Type: application/xop+xml; charset=utf-8\r\n'
                       b'Content-ID: <root@x>\r\n\r\n<root xmlns:x="http://www.w3.org/2004/08/xop/include"><x:Include href="cid:blob@x"/></root>\r\n'
                       b'--xyz\r\nContent-Type: application/octet-stream\r\nContent-ID: <blob@x>\r\n'
                       b'Content-Transfer-Encoding: binary\r\n\r\n\x00\xff\x10\r\n--xyz--\r\n')
            (run / "files/a.request.body.bin").write_bytes(payload)
            flow = json.loads((run / "flows" / "a.json").read_text())
            flow["request"] = {"headers": {"content-type": 'multipart/related; boundary="xyz"'}}
            out = Path(tmp) / "out"
            out.mkdir()
            n = extract_flow(run, flow, "request", out)
            self.assertEqual(n, 2)
            binaries = list(out.glob("*.bin"))
            self.assertEqual(len(binaries), 1)
            self.assertIn(b"\x00\xff\x10", binaries[0].read_bytes())

    def test_binary_cid_lookalike_is_not_an_xop_reference(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp))
            payload = (b'--xyz\r\nContent-Type: application/xml\r\nContent-ID: <root>\r\n\r\n'
                       b'<root/>\r\n--xyz\r\nContent-Type: application/octet-stream\r\n'
                       b'Content-ID: <binary>\r\nContent-Transfer-Encoding: binary\r\n\r\n'
                       b'\x00href="cid:missing"\xff\r\n--xyz--\r\n')
            (run / "files/a.request.body.bin").write_bytes(payload)
            flow = json.loads((run / "flows" / "a.json").read_text())
            flow["request"]["headers"] = {"content-type": 'multipart/related; boundary="xyz"'}
            out = Path(tmp) / "out"
            out.mkdir()
            self.assertEqual(extract_flow(run, flow, "request", out), 2)

    def test_unresolved_xop_is_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp))
            payload = (b'--xyz\r\nContent-Type: application/xop+xml\r\n\r\n'
                       b'<root xmlns:x="http://www.w3.org/2004/08/xop/include"><x:Include href="cid:missing"/></root>'
                       b'\r\n--xyz--\r\n')
            (run / "files/a.request.body.bin").write_bytes(payload)
            flow = json.loads((run / "flows" / "a.json").read_text())
            flow["request"] = {"headers": {"content-type": 'multipart/related; boundary="xyz"'}}
            with self.assertRaises(CaptureError):
                extract_flow(run, flow, "request", Path(tmp))

    def test_gzip_decompression_limit(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp), gzip.compress(b"a" * (32 * 1024 * 1024 + 1)))
            flow = json.loads((run / "flows" / "a.json").read_text())
            flow["request"] = {"headers": {"content-type": "text/xml", "content-encoding": "gzip"}}
            out = Path(tmp) / "out"
            out.mkdir()
            with self.assertRaises(CaptureError):
                extract_flow(run, flow, "request", out)

    def test_rejects_truncated_compression_and_mime(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp), gzip.compress(b"hello")[:-3])
            flow = json.loads((run / "flows" / "a.json").read_text())
            flow["request"]["headers"] = {"content-type": "text/xml", "content-encoding": "gzip"}
            out = Path(tmp) / "out"
            out.mkdir()
            with self.assertRaises(CaptureError):
                extract_flow(run, flow, "request", out)

            (run / "files/a.request.body.bin").write_bytes(
                b'--xyz\r\nContent-Type: application/xml\r\n\r\n<root/>\r\n')
            flow["request"]["headers"] = {"content-type": 'multipart/related; boundary="xyz"'}
            with self.assertRaises(CaptureError):
                extract_flow(run, flow, "request", out)

    def test_inspect_refuses_existing_output_and_removes_partial_on_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = self.make_run(Path(tmp))
            output = Path(tmp) / "out"
            output.mkdir()
            _, errors = inspect_run(run, output)
            self.assertIn("output directory already exists", errors)


if __name__ == "__main__":
    unittest.main()
