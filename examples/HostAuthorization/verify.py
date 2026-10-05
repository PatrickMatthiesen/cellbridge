#!/usr/bin/env python3
"""Run the disposable host policy example through its own isolated Aspire AppHost."""
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import subprocess
import time
import urllib.error
import urllib.request
import urllib.parse

ROOT = Path(__file__).resolve().parent
APPHOST = ROOT / "AppHost/AppHost.csproj"
RESOURCE = "0a0a19bc-9b47-4183-8764-41c117bb1f14"


def main():
    env = os.environ.copy()
    for name in list(env):
        if name.lower().startswith(("parameters__", "authentication__", "aspnetcore_", "apphost__")):
            env.pop(name)
    key = secrets.token_urlsafe(48)
    env["Parameters__SigningKey"] = key

    def cli(*args):
        return subprocess.run(["aspire", *args, "--apphost", str(APPHOST), "--non-interactive"],
            cwd=ROOT, env=env, check=True, capture_output=True, text=True, timeout=600).stdout

    def cli_json(raw):
        for index, character in enumerate(raw):
            if character in "[{":
                try:
                    return json.loads(raw[index:])
                except json.JSONDecodeError:
                    pass
        raise RuntimeError("Aspire did not return JSON.")

    hosts = cli_json(subprocess.run(["aspire", "ps", "--format", "Json", "--non-interactive"],
        cwd=ROOT, env=env, check=True, capture_output=True, text=True, timeout=30).stdout)
    if any(Path(host["appHostPath"]).resolve() == APPHOST.resolve() for host in hosts):
        raise RuntimeError("Stop this example's existing AppHost before running its disposable check.")

    def token(subject):
        def encode(value):
            return base64.urlsafe_b64encode(json.dumps(value, separators=(",", ":")).encode()).rstrip(b"=")
        data = encode({"alg": "HS256", "typ": "JWT"}) + b"." + encode({"iss": "cellbridge-host-example",
            "aud": "cellbridge", "sub": subject, "exp": int(time.time()) + 300})
        return (data + b"." + base64.urlsafe_b64encode(hmac.new(key.encode(), data, hashlib.sha256).digest()).rstrip(b"=")).decode()

    started = False
    try:
        started = True
        cli("start", "--isolated")
        cli("wait", "authorization-consumer")
        raw = cli("describe", "--format", "Json")
        description = cli_json(raw)
        resource = next(r for r in description["resources"] if r.get("displayName") == "authorization-consumer")
        origin = next(u["url"] for u in resource["urls"] if u.get("name") == "http" and
            urllib.parse.urlsplit(u["url"]).hostname in ("localhost", "127.0.0.1"))

        def request(subject, path, expected, body=None):
            req = urllib.request.Request(origin.rstrip("/") + path,
                data=None if body is None else json.dumps(body).encode(),
                headers={"Authorization": "Bearer " + token(subject), "Content-Type": "application/json"})
            try:
                response = urllib.request.urlopen(req)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                assert response.status == expected, (path, response.status, expected)
                return response.read()

        request("writer", "/shared/example.txt", 200)
        request("reader", "/shared/example.txt", 200)
        request("denied", "/shared/example.txt", 403)
        assert len(json.loads(request("writer", "/example/catalog", 200))["documents"]) == 1
        assert json.loads(request("denied", "/example/catalog", 200))["documents"] == []
        update = {"expectedRevision": 1, "nextRevision": 2, "access": "none"}
        request("writer", "/example/permissions/" + RESOURCE, 403, update)
        request("permission-operator", "/example/permissions/" + RESOURCE, 200, update)
        request("writer", "/shared/example.txt", 403)
        request("reader", "/shared/example.txt", 200)
        assert json.loads(request("writer", "/example/catalog", 200))["documents"] == []
        print("Host authorization example passed: access, denial, catalog and coordinated revocation.")
    finally:
        if started:
            cli("stop")


if __name__ == "__main__":
    main()
