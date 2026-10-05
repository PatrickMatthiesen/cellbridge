"""Independent Python oracle following MS-PCCRC 2.3 and examples 3.2/3.4.

No CellBridge assembly or serializer is used. Fixture input is public synthetic
content. The public test secret is SHA256(b'CellBridge wire hash vector secret').
"""
import hashlib
import json
from pathlib import Path
import struct


def encode(content, secret):
    descriptions, blocks = [], []
    for offset in range(0, len(content), 32 * 1024 * 1024):
        segment = content[offset:offset + 32 * 1024 * 1024]
        hashes = b''.join(hashlib.sha256(segment[i:i + 65536]).digest()
                          for i in range(0, len(segment), 65536))
        hod = hashlib.sha256(hashes).digest()
        descriptions.append(struct.pack('<QII', offset, len(segment), 65536)
                            + hod + hashlib.sha256(hod + secret).digest())
        blocks.append(struct.pack('<I', len(hashes) // 32) + hashes)
    return (struct.pack('<HIIII', 0x100, 0x800c, 0, 0, len(descriptions))
            + b''.join(descriptions) + b''.join(blocks))


def main():
    secret = hashlib.sha256(b'CellBridge wire hash vector secret').digest()
    vectors = []
    for length in [3, 65535, 65536, 65537, 128000, 33554432, 33554433]:
        content = (bytes(range(251)) * ((length + 250) // 251))[:length]
        encoded = encode(content, secret)
        vectors.append({'length': length, 'encodedLength': len(encoded),
                        'encodedSha256': hashlib.sha256(encoded).hexdigest(),
                        'hex': encoded.hex() if length in (3, 128000) else None})
    result = {'secretHex': secret.hex(), 'pattern': 'byte[i] = i % 251', 'vectors': vectors}
    Path(__file__).with_name('vectors.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__':
    main()
