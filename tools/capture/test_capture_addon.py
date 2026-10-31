import json
import os
import tempfile
from types import SimpleNamespace

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


def test_local_manifest_records_selection_and_remains_valid_for_completed_flow(tmp_path):
    from inspect_capture import validate_run

    addon = CaptureAddon(env(tmp_path, CAPTURE_LOCAL_PROCESSES="WINWORD.EXE,456", CAPTURE_HTTP2="true"))
    flow = make_flow()
    addon.requestheaders(flow)
    addon.request(flow)
    flow.response = http.Response.make(200)
    addon.responseheaders(flow)
    addon.response(flow)
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["proxy_mode"] == "local"
    assert manifest["local_processes"] == ["WINWORD.EXE", "456"]
    assert manifest["http2"] is True
    assert manifest["reverse_upstream"] is None
    assert validate_run(tmp_path) == (1, [])


def test_local_decoded_host_mismatch_remains_fail_closed_and_incomplete(tmp_path):
    from inspect_capture import validate_run

    addon = CaptureAddon(env(tmp_path, CAPTURE_LOCAL_PROCESSES="WINWORD.EXE"))
    flow = make_flow("unrelated.test")
    flow.live = True
    addon.requestheaders(flow)
    addon.done()
    assert flow.error is not None
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["status"] == "incomplete"
    assert "destination_not_allowlisted" in manifest["errors"]
    assert manifest["flow_count"] == 0
    assert {"error": "destination_not_allowlisted", "host": "unrelated.test", "port": 443} in manifest["transport_error_details"]
    assert validate_run(tmp_path)[1]


def test_local_transparent_request_uses_exact_authority_when_destination_is_ip(tmp_path):
    addon = CaptureAddon(env(tmp_path, CAPTURE_LOCAL_PROCESSES="WINWORD.EXE"))
    flow = make_flow("sharepoint.test")
    flow.request.host = "52.108.8.12"
    flow.request.host_header = "sharepoint.test"
    flow.client_conn.sni = "sharepoint.test"
    addon.requestheaders(flow)
    assert flow.error is None
    assert len(addon.exchanges) == 1
    metadata = addon.exchanges[next(iter(addon.exchanges))]["request"]
    assert metadata["host"] == "sharepoint.test"
    assert metadata["destination_ip"] == "52.108.8.12"
    addon.done()


def test_local_transparent_request_rejects_sni_host_mismatch_and_records_names_only(tmp_path):
    addon = CaptureAddon(env(tmp_path, CAPTURE_LOCAL_PROCESSES="WINWORD.EXE"))
    flow = make_flow("sharepoint.test")
    flow.request.host = "52.108.8.12"
    flow.request.host_header = "sharepoint.test"
    flow.client_conn.sni = "other.test"
    flow.live = True
    addon.requestheaders(flow)
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    detail = manifest["transport_error_details"][0]
    assert detail == {
        "error": "destination_not_allowlisted", "host": "52.108.8.12", "port": 443,
        "sni": "other.test", "host_header": "sharepoint.test",
    }
    assert manifest["flow_count"] == 0
    assert "secret" not in json.dumps(manifest)


def test_local_transparent_host_identity_accepts_bytes_authority(tmp_path):
    addon = CaptureAddon(env(tmp_path, CAPTURE_LOCAL_PROCESSES="WINWORD.EXE"))
    flow = make_flow("sharepoint.test")
    flow.request.host = "52.108.8.12"
    flow.request.host_header = "sharepoint.test"
    flow.request.data.authority = b"sharepoint.test:443"
    flow.client_conn.sni = "sharepoint.test"
    addon.requestheaders(flow)
    metadata = addon.exchanges[next(iter(addon.exchanges))]["request"]
    assert metadata["host"] == "sharepoint.test"
    assert metadata["destination_ip"] == "52.108.8.12"
    addon.done()


def test_local_transparent_request_rejects_malformed_authority(tmp_path):
    addon = CaptureAddon(env(tmp_path, CAPTURE_LOCAL_PROCESSES="WINWORD.EXE"))
    flow = make_flow("sharepoint.test")
    flow.request.host = "52.108.8.12"
    flow.request.host_header = "user:secret@sharepoint.test/path"
    flow.client_conn.sni = "sharepoint.test"
    flow.live = True
    addon.requestheaders(flow)
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["flow_count"] == 0
    assert manifest["transport_error_details"][0]["host_header"] == "[unavailable]"
    assert "secret" not in json.dumps(manifest)


def test_local_shutdown_with_pending_flow_still_reports_incomplete(tmp_path):
    from inspect_capture import validate_run

    addon = CaptureAddon(env(tmp_path, CAPTURE_LOCAL_PROCESSES="WINWORD.EXE"))
    addon.requestheaders(make_flow())
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["status"] == "incomplete"
    assert manifest["pending_count"] == 1
    assert validate_run(tmp_path)[1]


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
    assert entry["request"]["path"] == "/_vti_bin/cellstorage.svc?[REDACTED]"
    assert flow.request.path.endswith("?secret=local-only")
    assert dict(entry["response"]["headers"])["WWW-Authenticate"] == "[REDACTED]"
    assert json.loads((tmp_path / "manifest.json").read_text())["status"] == "complete"


def test_query_metadata_keeps_only_safe_parameter_names_and_validates(tmp_path):
    from inspect_capture import validate_run

    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    original = (
        "/_vti_bin/cellstorage.svc?access_token=SECRET_ACCESS_VALUE"
        "&WOPIsrc=https%3A%2F%2Fprivate.example%2Fdoc%3Ftoken%3DNESTED_SECRET"
        "&clientId=CLIENT_SECRET&clientId=SECOND_VALUE&password=PASSWORD_SECRET"
        "&eyJhbGciOiJIUzI1NiJ9=OPAQUE_VALUE"
        "&0123456789abcdef0123456789abcdef=HEX_VALUE"
        "&123e4567-e89b-12d3-a456-426614174000=GUID_VALUE"
        "&bad%20name=INVALID_NAME_VALUE#FRAGMENT_SECRET"
    )
    flow.request.path = original
    addon.requestheaders(flow)
    addon.request(flow)
    flow.response = http.Response.make(200)
    addon.responseheaders(flow)
    addon.response(flow)
    addon.done()

    entry = json.loads(next((tmp_path / "flows").glob("*.json")).read_text())
    request = entry["request"]
    assert request["path"].endswith("?[REDACTED]#[REDACTED]")
    assert request["query_parameter_names"] == ["access_token", "WOPIsrc", "clientId", "password"]
    assert flow.request.path == original
    serialized = json.dumps(entry)
    for secret in ("SECRET_ACCESS_VALUE", "private.example", "NESTED_SECRET", "CLIENT_SECRET",
                   "SECOND_VALUE", "PASSWORD_SECRET", "OPAQUE_VALUE", "HEX_VALUE", "GUID_VALUE",
                   "INVALID_NAME_VALUE", "FRAGMENT_SECRET"):
        assert secret not in serialized
    assert validate_run(tmp_path) == (1, [])


def test_query_parameter_name_extraction_is_bounded_and_deduplicates(tmp_path):
    names = capture_addon._query_parameter_names(
        "/x?" + "&".join(["access_token=x"] * 100 + [f"name{i}=v" for i in range(100)])
    )
    assert names == ["access_token"]
    assert len(capture_addon._query_parameter_names("/x?" + "&".join(f"key{i}=v" for i in range(80)))) == 32
    assert capture_addon._query_parameter_names("/x?" + "a" * 8193 + "=v") == []
    assert capture_addon._query_parameter_names("/x?" + "x" * 65 + "=v&docId=ok") == ["docId"]
    assert capture_addon._query_parameter_names("/x?docId=ok&negotiateVersion=1") == ["docId", "negotiateVersion"]


@pytest.mark.parametrize("referer", [
    "https://user:password@sharepoint.test/view?access_token=secret#private",
    "https://user:pass@word@sharepoint.test/view?access_token=secret#private",
])
def test_url_credentials_are_redacted_in_metadata_without_changing_headers(tmp_path, referer):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    flow.request.headers["Referer"] = referer
    addon.requestheaders(flow)
    addon.request(flow)
    location = "/document?unknown_credential=secret"
    flow.response = http.Response.make(302, headers={"Location": location})
    addon.responseheaders(flow)
    addon.response(flow)
    addon.done()
    entry = json.loads(next((tmp_path / "flows").glob("*.json")).read_text())
    assert dict(entry["request"]["headers"])["Referer"] == "https://[REDACTED]@sharepoint.test/view?[REDACTED]#[REDACTED]"
    assert dict(entry["response"]["headers"])["Location"] == "/document?[REDACTED]"
    assert flow.request.headers["Referer"] == referer
    assert flow.response.headers["Location"] == location


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


def test_tls_failure_records_only_safe_destination_metadata(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    data = SimpleNamespace(
        context=SimpleNamespace(client=SimpleNamespace(sni="rtc.officeapps.test"),
                                server=SimpleNamespace(address=("52.108.8.12", 443))),
        conn=SimpleNamespace(address=("127.0.0.1", 443),
                              error="The client does not trust the proxy's certificate for rtc.officeapps.test (unknown ca)"),
        error=None,
    )
    addon.tls_failed_client(data)
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["transport_error_details"] == [
        {"error": "tls_failed_client", "host": "rtc.officeapps.test", "port": 443,
         "cause": "client_unknown_ca"}
    ]
    assert "secret" not in json.dumps(manifest)
    assert "private.example" not in json.dumps(manifest)


@pytest.mark.parametrize("message", [
    "The client disconnected during the handshake. If this happens consistently for rtc.officeapps.test, this may indicate that the client does not trust the proxy's certificate.",
    "connection closed early",
    "EOF occurred in violation of protocol",
])
def test_client_tls_disconnect_wrapper_is_not_classified_as_certificate_rejection(tmp_path, message):
    addon = CaptureAddon(env(tmp_path))
    data = SimpleNamespace(
        context=SimpleNamespace(client=SimpleNamespace(sni="rtc.officeapps.test"),
                                server=SimpleNamespace(address=("52.108.8.12", 443))),
        conn=SimpleNamespace(error=message),
    )
    addon.tls_failed_client(data)
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["transport_error_details"][0]["cause"] == "connection_closed"
    assert message not in json.dumps(manifest)


def test_tls_failure_redacts_unusable_destination_values(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    data = SimpleNamespace(
        context=SimpleNamespace(client=SimpleNamespace(sni="user:secret@example.test/path")),
        conn=SimpleNamespace(address=None),
    )
    addon.tls_failed_client(data)
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["transport_error_details"] == [
        {"error": "tls_failed_client", "host": "[unavailable]", "port": None,
         "cause": "other"}
    ]
    assert "secret" not in json.dumps(manifest)


def test_upstream_tls_failure_classifies_certificate_validation_without_raw_error(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    data = SimpleNamespace(
        conn=SimpleNamespace(address=("192.0.2.10", 443), error="certificate verify failed: self-signed certificate"),
        error=None,
    )
    addon.tls_failed_server(data)
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["transport_error_details"] == [
        {"error": "tls_failed_server", "host": "192.0.2.10", "port": 443,
         "cause": "upstream_certificate_rejected"}
    ]
    assert "secret" not in json.dumps(manifest)


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


def test_late_websocket_end_and_flow_error_cannot_resurrect_running_manifest(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    flow = make_flow()
    flow.live = True
    addon.requestheaders(flow)
    addon.request(flow)
    flow.response = http.Response.make(101, headers={"Upgrade": "websocket", "Connection": "Upgrade"})
    addon.responseheaders(flow)
    addon.response(flow)
    from mitmproxy.websocket import WebSocketData
    flow.websocket = WebSocketData()
    addon.websocket_start(flow)
    addon.done()
    before = json.loads((tmp_path / "manifest.json").read_text())
    assert before["status"] == "incomplete"
    assert before["pending_count"] == 1

    flow.websocket.close_code = 1006
    flow.websocket.closed_by_client = False
    flow.websocket.timestamp_end = 1.0
    flow.error = Error("connection closed")
    addon.websocket_end(flow)
    addon.error(flow)
    addon._complete(addon.flow_ids[str(flow.id)], flow)

    after = json.loads((tmp_path / "manifest.json").read_text())
    assert after["status"] == "incomplete"
    assert after["pending_count"] == 0
    assert "websocket_abnormal_end" in after["errors"]


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
