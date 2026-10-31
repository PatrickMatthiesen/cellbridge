import sys
import json
import os
import re
from types import SimpleNamespace

import pytest

import run


@pytest.mark.parametrize("source", [None, "[]", "{}", '{"archive_info": []}',
    '{"url": "https://unreviewed.test/mitmproxy.tar.gz", "archive_info": {"hashes": {"sha256": "wrong"}}}'])
def test_launcher_rejects_unverified_source(monkeypatch, capsys, source):
    monkeypatch.setattr(sys, "argv", ["run.py", "--hosts", "sharepoint.test"])
    monkeypatch.setattr(run.importlib.metadata, "version", lambda _: run.MITMPROXY_VERSION)
    monkeypatch.setattr(run.importlib.metadata, "distribution", lambda _: SimpleNamespace(read_text=lambda _: source))
    with pytest.raises(SystemExit) as error:
        run.main()
    assert error.value.code == 2
    assert "hash-verified mitmproxy source" in capsys.readouterr().err


@pytest.mark.parametrize("upstream", [
    "ftp://sharepoint.test", "https://user:password@sharepoint.test",
    "https://sharepoint.test/path", "https://sharepoint.test?query=1",
    "https://sharepoint.test#fragment", "https://sharepoint.test:0",
])
def test_reverse_requires_http_origin(monkeypatch, upstream):
    monkeypatch.setattr(sys, "argv", ["run.py", "--reverse-upstream", upstream])
    with pytest.raises(SystemExit) as error:
        run.main()
    assert error.value.code == 2


def test_reverse_requires_existing_certificate(monkeypatch, tmp_path):
    monkeypatch.delenv("CAPTURE_HOSTS", raising=False)
    monkeypatch.setattr(sys, "argv", ["run.py", "--reverse-upstream", "https://sharepoint.test",
        "--tls-cert", str(tmp_path / "missing.crt"), "--tls-key", str(tmp_path / "missing.key")])
    with pytest.raises(SystemExit) as error:
        run.main()
    assert error.value.code == 2


def test_reverse_rejects_mismatched_allowlist(monkeypatch, capsys):
    monkeypatch.setattr(sys, "argv", ["run.py", "--reverse-upstream", "https://sharepoint.test",
        "--hosts", "other.test"])
    with pytest.raises(SystemExit) as error:
        run.main()
    assert error.value.code == 2
    assert "must match the fixed upstream" in capsys.readouterr().err


@pytest.mark.parametrize("option", ["--max-websocket-message-bytes", "--max-websocket-messages"])
@pytest.mark.parametrize("value", ["0", "-1"])
def test_websocket_limits_require_positive_values(monkeypatch, option, value):
    monkeypatch.setattr(sys, "argv", ["run.py", "--hosts", "sharepoint.test", option, value])
    with pytest.raises(SystemExit) as error:
        run.main()
    assert error.value.code == 2


@pytest.mark.parametrize("value", [
    "", "all", "ALL", "none", "0", "00", "-12", "!WINWORD.EXE", "*", "WIN*.EXE",
    "WINWORD.EXE,", ",WINWORD.EXE", "WINWORD.EXE,,123", " WINWORD.EXE", "WINWORD.EXE, 123",
    "C:\\Office\\WINWORD.EXE", "/usr/bin/word", "WINWORD.EXE /safe", "WINWORD.EXE\n",
    "WINWORD.EXE@8877", "WINWORD.EXE:foo",
])
def test_local_processes_reject_unbounded_or_nonprocess_specs(monkeypatch, value):
    monkeypatch.setattr(sys, "argv", ["run.py", "--hosts", "sharepoint.test", f"--local-processes={value}"])
    with pytest.raises(SystemExit) as error:
        run.main()
    assert error.value.code == 2


@pytest.mark.parametrize("value", ["WINWORD.EXE", "123", "WINWORD.EXE,OneDrive.exe,456", "7zFM.exe"])
def test_local_process_spec_is_accepted_by_installed_engine(value):
    from mitmproxy.proxy.mode_specs import ProxyMode

    assert run.parse_local_processes(value) == value
    assert ProxyMode.parse(f"local:{value}").data == value


@pytest.mark.parametrize("from_environment", [False, True])
def test_local_and_reverse_modes_are_mutually_exclusive(monkeypatch, capsys, from_environment):
    args = ["run.py", "--hosts", "sharepoint.test", "--local-processes", "WINWORD.EXE"]
    if from_environment:
        monkeypatch.setenv("CAPTURE_REVERSE_UPSTREAM", "https://sharepoint.test")
    else:
        args.extend(["--reverse-upstream", "https://sharepoint.test"])
    monkeypatch.setattr(sys, "argv", args)
    with pytest.raises(SystemExit) as error:
        run.main()
    assert error.value.code == 2
    assert "mutually exclusive" in capsys.readouterr().err


@pytest.fixture
def launch(monkeypatch, tmp_path):
    import mitmproxy.tools.main

    monkeypatch.delenv("CAPTURE_REVERSE_UPSTREAM", raising=False)
    monkeypatch.delenv("CAPTURE_TLS_CERT", raising=False)
    monkeypatch.delenv("CAPTURE_TLS_KEY", raising=False)
    monkeypatch.delenv("CAPTURE_UPSTREAM_CA", raising=False)
    monkeypatch.setattr(run.importlib.metadata, "version", lambda _: run.MITMPROXY_VERSION)
    source = json.dumps({"url": run.MITMPROXY_SOURCE_URL,
                         "archive_info": {"hashes": {"sha256": run.MITMPROXY_SOURCE_HASH}}})
    monkeypatch.setattr(run.importlib.metadata, "distribution", lambda _: SimpleNamespace(read_text=lambda _: source))
    monkeypatch.setattr(run.signal, "signal", lambda *_: None)
    # Restore launcher-exported settings after each test.
    for name in ("CAPTURE_OUTPUT", "CAPTURE_HOSTS", "CAPTURE_MAX_BODY_BYTES", "CAPTURE_MAX_TOTAL_BYTES",
                 "CAPTURE_MAX_WEBSOCKET_MESSAGE_BYTES", "CAPTURE_MAX_WEBSOCKET_MESSAGES",
                 "CAPTURE_REVERSE_UPSTREAM", "CAPTURE_LOCAL_PROCESSES", "CAPTURE_HTTP2"):
        monkeypatch.delenv(name, raising=False)
    output = tmp_path / "evidence"
    observed = []
    monkeypatch.setattr(mitmproxy.tools.main, "mitmdump", lambda options: observed.extend(options))

    def invoke(*options):
        monkeypatch.setattr(sys, "argv", ["run.py", "--hosts", "sharepoint.test,rtc.example.test",
            "--output-root", str(output), *options])
        assert run.main() == 0
        return observed, output
    return invoke


@pytest.mark.parametrize("http2", [False, True])
def test_local_launcher_limits_processes_and_hosts_and_reuses_ca(launch, http2):
    from mitmproxy.options import Options

    extras = ["--http2"] if http2 else []
    options, output = launch("--local-processes", "WINWORD.EXE,456", *extras)
    assert options[options.index("--mode") + 1] == "local:WINWORD.EXE,456"
    assert "--listen-host" not in options and "--listen-port" not in options
    parsed = Options()
    parsed.set(*(options[i + 1] for i, option in enumerate(options) if option == "--set"), defer=True)
    assert parsed.allow_hosts == [r"^sharepoint\.test(?::[0-9]+)?$", r"^rtc\.example\.test(?::[0-9]+)?$"]
    assert parsed.confdir == str(output / ".mitmproxy")
    assert parsed.http2 is http2
    assert parsed.show_ignored_hosts is False
    assert os.environ["CAPTURE_LOCAL_PROCESSES"] == "WINWORD.EXE,456"
    assert os.environ["CAPTURE_REVERSE_UPSTREAM"] == ""
    assert os.environ["CAPTURE_HTTP2"] == str(http2).lower()
    assert os.environ["CAPTURE_MAX_WEBSOCKET_MESSAGES"] == "10000"


def test_forward_launcher_keeps_default_behavior_and_clears_local_selection(launch, monkeypatch):
    monkeypatch.setenv("CAPTURE_LOCAL_PROCESSES", "WINWORD.EXE")
    options, _ = launch()
    assert options[options.index("--mode") + 1] == "regular"
    assert options[options.index("--listen-host") + 1] == "127.0.0.1"
    assert "http2=false" in options
    assert not any(option.startswith("allow_hosts=") for option in options)
    assert os.environ["CAPTURE_LOCAL_PROCESSES"] == ""


def test_reverse_launcher_keeps_fixed_upstream_and_temporary_certificate(launch, tmp_path):
    cert, key = tmp_path / "certificate.pem", tmp_path / "key.pem"
    cert.write_bytes(b"certificate")
    key.write_bytes(b"private key")
    options, _ = launch("--hosts", "sharepoint.test", "--reverse-upstream", "https://sharepoint.test",
                        "--tls-cert", str(cert), "--tls-key", str(key))
    assert options[options.index("--mode") + 1] == "reverse:https://sharepoint.test"
    assert "--listen-host" in options and "http2=false" in options
    assert not any(option.startswith("allow_hosts=") for option in options)
    assert "keep_host_header=true" in options
    combined = options[options.index("--certs") + 1].removeprefix("*=")
    from pathlib import Path
    assert not Path(combined).exists()
    assert os.environ["CAPTURE_REVERSE_UPSTREAM"] == "https://sharepoint.test"
    assert os.environ["CAPTURE_LOCAL_PROCESSES"] == ""


@pytest.mark.parametrize("host,expected", [
    ("sharepoint.test", True), ("SHAREPOINT.TEST:443", True), ("sharepoint.test:8443", True),
    ("othersharepoint.test:443", False), ("sharepoint.test.evil:443", False), ("sharepointXtest:443", False),
])
def test_allow_host_regex_matches_only_exact_host_with_optional_port(host, expected):
    assert bool(re.search(run.allow_host_pattern("sharepoint.test"), host, re.IGNORECASE)) is expected


@pytest.mark.parametrize("sni", ["unrelated.test", "sharepoint.test.evil"])
def test_installed_engine_passes_unmatched_tls_through_without_http_capture(monkeypatch, sni):
    from mitmproxy import connection, ctx
    from mitmproxy.addons.next_layer import NextLayer
    from mitmproxy.options import Options
    from mitmproxy.proxy.context import Context
    from mitmproxy.proxy.layers import TCPLayer
    from mitmproxy.proxy.layers.modes import TransparentProxy

    options = Options(allow_hosts=[run.allow_host_pattern("sharepoint.test")], show_ignored_hosts=False)
    monkeypatch.setattr(ctx, "options", options, raising=False)
    next_layer = NextLayer()
    next_layer.configure({"allow_hosts"})
    client = connection.Client(peername=("127.0.0.1", 1234), sockname=("127.0.0.1", 8877))
    context = Context(client, options)
    context.server.address = ("192.0.2.1", 443)
    TransparentProxy(context)
    monkeypatch.setattr(next_layer, "_get_client_hello", lambda *_: SimpleNamespace(sni=sni))
    assert next_layer._ignore_connection(context, b"", b"") is True
    layer = next_layer._next_layer(context, b"", b"")
    assert isinstance(layer, TCPLayer)
    assert layer.flow is None
