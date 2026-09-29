import json
import os
import tempfile

import pytest
from mitmproxy import connection, http
from mitmproxy.flow import Error

_default_output = tempfile.mkdtemp(prefix="office-capture-test-")
os.environ.setdefault("CAPTURE_OUTPUT", _default_output)
os.environ.setdefault("CAPTURE_HOSTS", "sharepoint.test")
from capture_addon import CaptureAddon
import capture_addon


def env(tmp_path, **extra):
    return {"CAPTURE_OUTPUT": str(tmp_path.resolve()), "CAPTURE_HOSTS": "sharepoint.test",
            "CAPTURE_MAX_BODY_BYTES": "100", "CAPTURE_MAX_TOTAL_BYTES": "200", **extra}


def make_flow(host="sharepoint.test", method="POST"):
    request = http.Request.make(method, f"https://{host}:443/_vti_bin/cellstorage.svc?secret=local-only",
                                headers=[(b"Content-Type", b"application/octet-stream"),
                                         (b"Authorization", b"Bearer secret"), (b"Cookie", b"sid=secret")])
    client = connection.Client(peername=("127.0.0.1", 1234), sockname=("127.0.0.1", 8877))
    server = connection.Server(address=(host, 443))
    flow = http.HTTPFlow(client, server)
    flow.request = request
    return flow


def test_streaming_preserves_binary_and_redacts_auth_headers(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    addon.requestheaders(flow)
    payload = b"\x00\xffsoap\x00\x80"
    assert flow.request.stream(payload) == payload
    addon.request(flow)
    flow.response = http.Response.make(200, headers=[(b"WWW-Authenticate", b"Negotiate secret")])
    addon.responseheaders(flow)
    reply = b"\xff\x00response"
    assert flow.response.stream(reply) == reply
    addon.response(flow)
    addon.done()

    bodies = list((tmp_path / "files").glob("*.body.bin"))
    assert {p.read_bytes() for p in bodies} == {payload, reply}
    entry = json.loads(next((tmp_path / "flows").glob("*.json")).read_text())
    assert entry["completeness"] == "complete"
    assert entry["sequence"] == 1
    assert entry["request_body"]["sha256"]
    redacted = {key: value for key, value in entry["request"]["headers"]}
    assert redacted["Authorization"] == "[REDACTED]"
    assert redacted["Cookie"] == "[REDACTED]"
    assert dict(entry["response"]["headers"])["WWW-Authenticate"] == "[REDACTED]"
    assert json.loads((tmp_path / "manifest.json").read_text())["status"] == "complete"


@pytest.mark.parametrize("method", ["GET", "HEAD"])
def test_empty_request_and_response_bodies_are_complete(tmp_path, method):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow(method=method)
    addon.requestheaders(flow)
    addon.request(flow)
    flow.response = http.Response.make(200)
    addon.responseheaders(flow)
    addon.response(flow)
    addon.done()
    entry = json.loads(next((tmp_path / "flows").glob("*.json")).read_text())
    assert entry["completeness"] == "complete"
    assert entry["request_body"]["captured_bytes"] == 0
    assert entry["response_body"]["captured_bytes"] == 0
    assert len(list((tmp_path / "files").glob("*.body.bin"))) == 2


def test_limits_truncate_capture_but_return_original_chunk(tmp_path):
    addon = CaptureAddon(env(tmp_path, CAPTURE_MAX_BODY_BYTES="3"))
    flow = make_flow()
    addon.requestheaders(flow)
    original = b"abcdefgh"
    assert flow.request.stream(original) == original
    addon.request(flow)
    entry = addon.exchanges[addon.flow_ids[str(flow.id)]]
    assert (tmp_path / entry["request_body"]["file"]).read_bytes() == b"abc"
    assert entry["request_body"]["original_bytes"] == 8
    assert entry["request_body"]["complete"] is False


def test_non_allowlisted_http_and_connect_are_killed(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow("other.test")
    flow.kill = lambda: setattr(flow, "metadata", {"killed": True})
    addon.http_connect(flow)
    assert flow.metadata["killed"]
    flow = make_flow("other.test")
    flow.kill = lambda: setattr(flow, "metadata", {"killed": True})
    addon.requestheaders(flow)
    assert flow.metadata["killed"]
    assert addon.exchanges == {}
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["status"] == "incomplete"
    assert "destination_not_allowlisted" in manifest["errors"]


def test_connection_error_records_incomplete_flow(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    addon.requestheaders(flow)
    flow.error = Error("upstream unavailable")
    addon.error(flow)
    addon.done()
    entry = json.loads(next((tmp_path / "flows").glob("*.json")).read_text())
    assert entry["error"]["message"] == "upstream unavailable"
    assert entry["completeness"] == "incomplete"
    assert json.loads((tmp_path / "manifest.json").read_text())["status"] == "incomplete"


def test_aborted_response_and_shutdown_remain_incomplete(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    addon.requestheaders(flow)
    addon.request(flow)
    flow.response = http.Response.make(200)
    addon.responseheaders(flow)
    flow.response.stream(b"partial")
    flow.error = Error("connection closed")
    addon.error(flow)
    addon.done()
    entry = json.loads(next((tmp_path / "flows").glob("*.json")).read_text())
    assert entry["response"]["status_code"] == 200
    assert entry["response_body"]["complete"] is False
    assert entry["completeness"] == "incomplete"


def test_pending_flow_is_flushed_on_shutdown(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    addon.requestheaders(make_flow())
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["pending_count"] == 1
    assert manifest["status"] == "incomplete"


def test_flow_cap_disables_capture_without_killing_allowed_traffic(tmp_path):
    addon = CaptureAddon(env(tmp_path, CAPTURE_MAX_FLOWS="1"))
    addon.requestheaders(make_flow())
    second = make_flow()
    addon.requestheaders(second)
    assert second.request.stream is True
    assert len(addon.exchanges) == 1
    second.response = http.Response.make(200)
    addon.responseheaders(second)
    assert second.response.stream is True
    assert addon.recording_healthy is False


def test_existing_run_directory_is_not_overwritten(tmp_path):
    CaptureAddon(env(tmp_path))
    with pytest.raises(ValueError, match="new run directory"):
        CaptureAddon(env(tmp_path))


def test_write_failure_is_recorded_and_does_not_change_forwarded_bytes(tmp_path, monkeypatch):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    addon.requestheaders(flow)
    sink = addon.sinks[(addon.flow_ids[str(flow.id)], "request")]
    class BrokenWriter:
        def write(self, _):
            raise OSError("disk full")
        def close(self):
            pass
    sink.file.close()
    sink.file = BrokenWriter()
    chunk = b"unchanged"
    assert flow.request.stream(chunk) == chunk
    addon.request(flow)
    addon.done()
    assert addon.recording_healthy is False
    assert addon.exchanges[addon.flow_ids[str(flow.id)]]["request_body"]["complete"] is False


@pytest.mark.parametrize("changes", [
    {"CAPTURE_OUTPUT": "relative/path"}, {"CAPTURE_HOSTS": ""},
    {"CAPTURE_MAX_BODY_BYTES": "-1"}, {"CAPTURE_MAX_FLOWS": "0"},
])
def test_rejects_invalid_configuration(tmp_path, changes):
    with pytest.raises(ValueError):
        CaptureAddon(env(tmp_path, **changes))


@pytest.mark.skipif(os.name != "nt", reason="Windows file sharing semantics")
@pytest.mark.parametrize("kind", ["manifest", "body"])
def test_windows_reader_lock_is_retried_without_losing_evidence(tmp_path, monkeypatch, kind):
    import ctypes
    from ctypes import wintypes

    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    addon.requestheaders(flow)
    flow.request.stream(b"wire bytes")
    sink = addon.sinks[(addon.flow_ids[str(flow.id)], "request")]
    target = tmp_path / "manifest.json" if kind == "manifest" else sink.temp
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    # Allow reading and writing, but deliberately deny delete/rename sharing.
    handle = kernel.CreateFileW(str(target), 0x80000000, 3, None, 3, 0, None)
    assert handle != wintypes.HANDLE(-1).value
    waits = []

    def release_reader(delay):
        nonlocal handle
        waits.append(delay)
        assert kernel.CloseHandle(handle)
        handle = None

    monkeypatch.setattr(capture_addon.time, "sleep", release_reader)
    try:
        if kind == "manifest":
            addon._write_manifest("running")
        addon.request(flow)
    finally:
        if handle is not None:
            kernel.CloseHandle(handle)
    assert waits == [0.01]
    assert addon.recording_healthy
    assert sink.path.read_bytes() == b"wire bytes"
    assert addon.exchanges[addon.flow_ids[str(flow.id)]]["request_body"]["complete"]


def test_persistent_replace_failure_is_bounded_and_diagnosed(tmp_path, monkeypatch):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    addon.requestheaders(flow)
    flow.request.stream(b"unchanged")
    real_replace = capture_addon.os.replace
    attempts = []
    waits = []

    def locked_replace(source, target):
        if str(source).endswith(".part"):
            attempts.append(source)
            error = PermissionError(13, "sharing violation")
            error.winerror = 32
            raise error
        return real_replace(source, target)

    monkeypatch.setattr(capture_addon.os, "replace", locked_replace)
    monkeypatch.setattr(capture_addon.time, "sleep", waits.append)
    addon.request(flow)
    addon.done()
    assert len(attempts) == 4
    assert sum(waits) == pytest.approx(0.14)
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["status"] == "incomplete"
    detail = manifest["recording_error_details"][0]
    assert detail["winerror"] == 32
    assert detail["operation"] == "body"
    assert detail["file"].startswith("files/")
    assert not addon.exchanges[addon.flow_ids[str(flow.id)]]["request_body"]["complete"]
