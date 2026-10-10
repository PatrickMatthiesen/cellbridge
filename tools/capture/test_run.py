import sys

import pytest

import run


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
