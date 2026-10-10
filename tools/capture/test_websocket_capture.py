"""WebSocket evidence integrity tests using mitmproxy's pinned public model."""
import json

import pytest
from mitmproxy import http, websocket
from mitmproxy.flow import Error
from wsproto.frame_protocol import Opcode

from test_capture_addon import env, make_flow
from capture_addon import CaptureAddon
from inspect_capture import inspect_run, validate_run


def start(addon):
    flow = make_flow(method="GET")
    addon.requestheaders(flow)
    addon.request(flow)
    flow.response = http.Response.make(101, headers={"Upgrade": "websocket"})
    addon.responseheaders(flow)
    flow.websocket = websocket.WebSocketData()
    addon.response(flow)
    addon.websocket_start(flow)
    return flow


def message(addon, flow, payload=b"hello", *, from_client=True, opcode=Opcode.TEXT, **flags):
    msg = websocket.WebSocketMessage(opcode, from_client, payload, **flags)
    flow.websocket.messages.append(msg)
    addon.websocket_message(flow)
    assert msg.content == payload
    return msg


def end(addon, flow, code=1000):
    flow.websocket.close_code = code
    flow.websocket.close_reason = "done"
    flow.websocket.closed_by_client = True
    flow.websocket.timestamp_end = 1720000000.5
    addon.websocket_end(flow)


def completed(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    flow = start(addon)
    message(addon, flow, "hi ø".encode())
    message(addon, flow, b"\x00\xff", from_client=False, opcode=Opcode.BINARY)
    end(addon, flow)
    addon.done()
    return addon, flow


def test_order_payloads_correlation_and_extraction(tmp_path):
    addon, flow = completed(tmp_path)
    fid = addon.flow_ids[flow.id]
    item = addon.exchanges[fid]
    paths = sorted((tmp_path / "websockets" / fid).glob("*.json"))
    messages = [json.loads(p.read_text()) for p in paths]
    assert [m["direction"] for m in messages] == ["client_to_server", "server_to_client"]
    assert [m["type"] for m in messages] == ["text", "binary"]
    assert [m["sequence"] for m in messages] == [1, 2]
    assert [m["event_sequence"] for m in messages] == [2, 3]
    assert all(m["session_id"] == addon.session_id and m["flow_id"] == fid for m in messages)
    assert item["request"]["client_connection_id"] == flow.client_conn.id
    assert item["response"]["server_connection_id"] == flow.server_conn.id
    assert item["mitmproxy_flow_id"] == flow.id
    assert item["websocket"]["end"]["event_sequence"] == 4
    assert validate_run(tmp_path) == (1, [])
    out = tmp_path.parent / (tmp_path.name + "-extracted")
    assert inspect_run(tmp_path, out) == (4, [])
    assert (out / fid / "websocket/00000001.txt").read_bytes() == "hi ø".encode()
    assert (out / fid / "websocket/00000002.bin").read_bytes() == b"\x00\xff"


def test_interleaved_connections_and_empty_messages(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    a, b = start(addon), start(addon)
    message(addon, a, b"")
    for _ in range(4):
        message(addon, b, b"b")
        assert len(b.websocket.messages) == 1
    message(addon, a, b"a")
    end(addon, b)
    end(addon, a)
    addon.done()
    assert validate_run(tmp_path) == (2, [])


@pytest.mark.parametrize("limits, expected", [
    ({"CAPTURE_MAX_WEBSOCKET_MESSAGE_BYTES": "2"}, "websocket_payload_incomplete"),
    ({"CAPTURE_MAX_TOTAL_BYTES": "2"}, "websocket_payload_incomplete"),
    ({"CAPTURE_MAX_WEBSOCKET_MESSAGES": "1"}, "max_websocket_messages_reached"),
])
def test_limits_preserve_messages_and_fail_validation(tmp_path, limits, expected):
    addon = CaptureAddon(env(tmp_path, **limits))
    flow = start(addon)
    for _ in range(4):
        msg = message(addon, flow, b"hello")
        assert not msg.dropped and not msg.injected
        assert len(flow.websocket.messages) == 1
    end(addon, flow)
    addon.done()
    assert expected in addon.recording_errors
    assert validate_run(tmp_path)[1]
    assert len(list((tmp_path / "websockets").glob("*/*.json"))) <= addon.max_ws_messages
    assert addon.total_captured <= addon.max_total


@pytest.mark.parametrize("code", [1006, 1002, 1011])
def test_abnormal_close_is_invalid(tmp_path, code):
    addon = CaptureAddon(env(tmp_path))
    flow = start(addon)
    end(addon, flow, code)
    addon.done()
    assert "websocket_abnormal_end" in addon.recording_errors
    assert validate_run(tmp_path)[1]


def test_missing_end_remains_pending_on_stop(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    flow = start(addon)
    message(addon, flow)
    addon.done()
    manifest = json.loads((tmp_path / "manifest.json").read_text())
    assert manifest["status"] == "incomplete" and manifest["pending_count"] == 1
    assert validate_run(tmp_path)[1]


@pytest.mark.parametrize("flags", [{"dropped": True}, {"injected": True}])
def test_modified_messages_are_recorded_but_invalid(tmp_path, flags):
    addon = CaptureAddon(env(tmp_path))
    flow = start(addon)
    msg = message(addon, flow, **flags)
    end(addon, flow)
    addon.done()
    assert msg.dropped == flags.get("dropped", False)
    assert "websocket_message_modified" in addon.recording_errors
    assert any("dropped/injected" in p for p in validate_run(tmp_path)[1])


def test_flow_error_is_preserved_at_end(tmp_path):
    addon = CaptureAddon(env(tmp_path))
    flow = start(addon)
    flow.error = Error("synthetic WebSocket error")
    end(addon, flow)
    addon.done()
    assert addon.exchanges[addon.flow_ids[flow.id]]["websocket"]["end"]["error"] == flow.error.msg
    assert validate_run(tmp_path)[1]


@pytest.mark.parametrize("damage", ["missing_record", "missing_payload", "truncated", "hash", "order",
    "session", "flow", "event", "direction", "type", "dropped", "timestamp", "start", "end", "summary", "manifest", "orphan"])
def test_offline_validation_detects_damage(tmp_path, damage):
    addon, flow = completed(tmp_path)
    fid = addon.flow_ids[flow.id]
    path = tmp_path / "websockets" / fid / "00000002.json"
    msg = json.loads(path.read_text())
    flow_path = tmp_path / "flows" / (fid + ".json")
    item = json.loads(flow_path.read_text())
    if damage == "missing_record":
        path.unlink()
    elif damage == "missing_payload":
        (tmp_path / msg["payload"]["file"]).unlink()
    elif damage == "truncated":
        (tmp_path / msg["payload"]["file"]).write_bytes(b"\x00")
    elif damage == "hash":
        msg["payload"]["sha256"] = "0" * 64
    elif damage in ("order", "session", "flow", "event", "direction", "type", "dropped", "timestamp"):
        key = {"order": "sequence", "session": "session_id", "flow": "flow_id", "event": "event_sequence", "timestamp": "received_at"}.get(damage, damage)
        msg[key] = "invalid"
    elif damage in ("start", "end"):
        del item["websocket"][damage]
    elif damage == "summary":
        item["websocket"]["observed_messages"] += 1
    elif damage == "manifest":
        manifest_path = tmp_path / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["websocket_count"] = 0
        manifest_path.write_text(json.dumps(manifest))
    elif damage == "orphan":
        orphan = tmp_path / "websockets/orphan/00000001.json"
        orphan.parent.mkdir()
        orphan.write_text(json.dumps(msg))
    if path.exists():
        path.write_text(json.dumps(msg))
    flow_path.write_text(json.dumps(item))
    assert validate_run(tmp_path)[1], damage


def test_http_upgrade_cannot_hide_missing_lifecycle(tmp_path):
    addon, flow = completed(tmp_path)
    path = tmp_path / "flows" / (addon.flow_ids[flow.id] + ".json")
    item = json.loads(path.read_text())
    del item["websocket"]
    del item["websocket_expected"]
    path.write_text(json.dumps(item))
    assert any("lifecycle is missing" in p for p in validate_run(tmp_path)[1])


def test_websocket_io_failure_does_not_mutate_forwarding(tmp_path, monkeypatch):
    addon = CaptureAddon(env(tmp_path))
    flow = start(addon)
    import capture_addon
    real_write = capture_addon._atomic_json
    def fail(path, item):
        if "websockets" in path.parts:
            raise OSError(28, "disk full")
        real_write(path, item)
    monkeypatch.setattr(capture_addon, "_atomic_json", fail)
    msg = message(addon, flow)
    assert not msg.dropped
    end(addon, flow)
    addon.done()
    assert not addon.recording_healthy
    assert validate_run(tmp_path)[1]


def test_http_and_websocket_share_total_budget(tmp_path):
    addon = CaptureAddon(env(tmp_path, CAPTURE_MAX_TOTAL_BYTES="6"))
    http_flow = make_flow()
    addon.requestheaders(http_flow)
    http_flow.request.stream(b"1234")
    addon.request(http_flow)
    http_flow.response = http.Response.make(200)
    addon.responseheaders(http_flow)
    addon.response(http_flow)
    ws_flow = start(addon)
    message(addon, ws_flow, b"hello")
    end(addon, ws_flow)
    addon.done()
    ws = addon.exchanges[addon.flow_ids[ws_flow.id]]["websocket"]
    assert ws["captured_bytes"] == 2
    assert addon.total_captured == 6
    assert validate_run(tmp_path)[1]


@pytest.mark.parametrize("limits", [
    {"CAPTURE_MAX_WEBSOCKET_MESSAGE_BYTES": "-1"}, {"CAPTURE_MAX_WEBSOCKET_MESSAGES": "0"},
])
def test_websocket_limits_reject_invalid_configuration(tmp_path, limits):
    with pytest.raises(ValueError):
        CaptureAddon(env(tmp_path, **limits))


def test_schema_one_websocket_upgrade_cannot_validate_as_http_only(tmp_path):
    addon, flow = completed(tmp_path)
    manifest_path = tmp_path / "manifest.json"
    manifest = json.loads(manifest_path.read_text())
    manifest["schema_version"] = 1
    manifest_path.write_text(json.dumps(manifest))
    flow_path = tmp_path / "flows" / (addon.flow_ids[flow.id] + ".json")
    item = json.loads(flow_path.read_text())
    item["schema_version"] = 1
    item.pop("websocket")
    item.pop("websocket_expected")
    flow_path.write_text(json.dumps(item))
    assert any("legacy HTTP-only" in p for p in validate_run(tmp_path)[1])


def test_total_byte_counter_and_limit_are_verified_against_files(tmp_path):
    completed(tmp_path)
    path = tmp_path / "manifest.json"
    manifest = json.loads(path.read_text())
    manifest["captured_body_bytes"] = 0
    manifest["limits"]["max_total_bytes"] = 1
    path.write_text(json.dumps(manifest))
    problems = validate_run(tmp_path)[1]
    assert any("captured_body_bytes does not match" in p for p in problems)
    assert any("max_total_bytes" in p for p in problems)


def test_websocket_capture_scope_is_required(tmp_path):
    completed(tmp_path)
    path = tmp_path / "manifest.json"
    manifest = json.loads(path.read_text())
    del manifest["websocket_capture_scope"]
    path.write_text(json.dumps(manifest))
    assert any("capture scope" in p for p in validate_run(tmp_path)[1])
