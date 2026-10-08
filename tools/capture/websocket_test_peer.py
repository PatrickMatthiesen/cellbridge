"""Small RFC 6455 test peers, independent of mitmproxy/wsproto.

Only the uncompressed, unextended frames used by the local tests are supported.
This is test infrastructure, not a general WebSocket implementation.
"""
import base64
import hashlib
import socket
import struct


GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


def accept(key):
    return base64.b64encode(hashlib.sha1((key + GUID).encode()).digest()).decode()


def frame(opcode, payload=b"", *, masked=False, final=True):
    header = bytes([(0x80 if final else 0) | opcode])
    size = len(payload)
    mask_bit = 0x80 if masked else 0
    if size < 126:
        header += bytes([mask_bit | size])
    elif size < 65536:
        header += bytes([mask_bit | 126]) + struct.pack("!H", size)
    else:
        header += bytes([mask_bit | 127]) + struct.pack("!Q", size)
    if masked:
        mask = b"\x12\x34\x56\x78"
        header += mask
        payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
    return header + payload


def exact(reader, count):
    data = reader.read(count)
    if len(data) != count:
        raise EOFError("test peer closed during frame")
    return data


def read_frame(reader, *, masked):
    first, second = exact(reader, 2)
    assert not first & 0x70, "test peers do not negotiate extensions"
    assert bool(second & 0x80) == masked
    size = second & 0x7f
    if size == 126:
        size = struct.unpack("!H", exact(reader, 2))[0]
    elif size == 127:
        size = struct.unpack("!Q", exact(reader, 8))[0]
    assert size <= 2 * 1024 * 1024, "test frame exceeds bound"
    mask = exact(reader, 4) if masked else None
    payload = exact(reader, size)
    if mask:
        payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
    return bool(first & 0x80), first & 0xf, payload


def serve(handler):
    key = handler.headers["Sec-WebSocket-Key"]
    handler.send_response(101)
    handler.send_header("Upgrade", "websocket")
    handler.send_header("Connection", "Upgrade")
    handler.send_header("Sec-WebSocket-Accept", accept(key))
    handler.end_headers()
    handler.wfile.flush()
    handler.close_connection = True
    handler.connection.settimeout(5)
    if handler.path.endswith("/ws-abort"):
        handler.wfile.write(frame(1, b"unfinished", final=False))
        handler.connection.shutdown(socket.SHUT_RDWR)
        return
    if handler.path.endswith("/ws-error"):
        handler.wfile.write(frame(3))  # Reserved opcode causes protocol error.
        return
    pending = bytearray()
    kind = None
    try:
        while True:
            final, opcode, payload = read_frame(handler.rfile, masked=True)
            if opcode == 8:
                handler.wfile.write(frame(8, payload))
                return
            if opcode == 9:
                handler.wfile.write(frame(10, payload))
                continue
            if opcode in (1, 2):
                assert kind is None
                kind = opcode
            else:
                assert opcode == 0 and kind is not None
            pending.extend(payload)
            assert len(pending) <= 2 * 1024 * 1024
            if final:
                if kind == 1:
                    pending.decode("utf-8")
                handler.wfile.write(frame(kind, bytes(pending)))
                pending.clear()
                kind = None
    except (OSError, EOFError):
        pass


class Client:
    def __init__(self, proxy_port, upstream_port, path="/ws", *, sock=None, host=None):
        self.sock = sock or socket.create_connection(("127.0.0.1", proxy_port), timeout=5)
        self.reader = self.sock.makefile("rb")
        key = base64.b64encode(b"synthetic-key-16").decode()
        host = host or f"localhost:{upstream_port}"
        target = path if sock else f"http://{host}{path}"
        self.sock.sendall((f"GET {target} HTTP/1.1\r\nHost: {host}\r\n"
            f"Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n"
            f"Sec-WebSocket-Key: {key}\r\n\r\n").encode())
        status = self.reader.readline()
        assert b" 101 " in status, status
        headers = {}
        while (line := self.reader.readline()) not in (b"\r\n", b""):
            name, value = line.decode().split(":", 1)
            headers[name.lower()] = value.strip()
        assert headers["sec-websocket-accept"] == accept(key)

    def send(self, opcode, payload=b"", *, final=True):
        self.sock.sendall(frame(opcode, payload, masked=True, final=final))

    def receive(self):
        final, opcode, payload = read_frame(self.reader, masked=False)
        assert final
        return opcode, payload

    def close(self):
        self.reader.close()
        self.sock.close()

    def finish(self, code=1000):
        self.send(8, struct.pack("!H", code) + b"local test done")
        opcode, payload = self.receive()
        assert opcode == 8 and struct.unpack("!H", payload[:2])[0] == code
        self.close()
