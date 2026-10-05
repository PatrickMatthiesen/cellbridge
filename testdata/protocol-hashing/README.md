# Wire-hash vectors

These are independently generated synthetic vectors, not SharePoint captures or
Microsoft output. `generate_vectors.py` uses Python `hashlib` and `struct`, without
calling CellBridge. Run `python3 testdata/protocol-hashing/generate_vectors.py` to
reproduce `vectors.json`.

The oracle follows [MS-PCCRC 2.3](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-pccrc/36171657-7f15-419e-973b-6612b1799117),
[segment descriptions](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-pccrc/9991a28f-1c6e-4473-bd51-14732e3b8390)
and [block arrays](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-pccrc/6aabffe5-f712-41fe-8f18-c992c9ba507e).
The [125-KB example](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-pccrc/2a914f3f-2b5b-473d-8080-660796918a27)
fixes the full-range offsets, two-block layout and field offsets for the 128000-byte
case. The [125-MB example](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-pccrc/bb13f226-e6a7-4ece-9ca1-53dbb4a4bc21)
confirms that segment descriptions precede all block arrays. This fixture uses zero
for the final full segment range, as allowed by 2.3. Input bytes and the hashed test
secret are specified in JSON. They contain no private data.

Boundary vectors retain a literal digest of the entire encoding to keep fixtures
small. The three-byte and 125-KB vectors retain every encoded byte. Tests compare
the independent result with the standalone .NET codec and use split object inputs
to check concatenation across block and segment boundaries.
