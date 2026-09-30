using CellBridge.FssHttpB;

byte[] b = new byte[] {
    0x0e,0x00,0x0b,0x00,0x9c,0xcf,0x29,0xf3,0x39,0x94,0x06,0x9b,0x06,0x02,0x00,0x00,
    0xee,0x02,0x00,0x00,0xaa,0x02,0x20,0x00,0x8c,0x10,0x84,0x19,0x93,0x4b,0xeb,0x4e,
    0xb3,0x20,0x91,0x94,0x32,0xd6,0xe5,0x93,0x5a,0x04,0x16,0x00,0x0d,0x6d,0x73,0x77,
    0x6f,0x72,0x64,0x07,0x77,0x69,0x6e,0x7a,0x02,0x08,0x00,0x46,0xf0,0x70,0x3b,0x77,
    0x01,0x16,0x02,0x06,0x00,0x03,0x05,0x00,0x8a,0x02,0x04,0x00,0x00,0xf0,0xda,0x02,
    0x06,0x00,0x03,0x00,0x00,0xca,0x02,0x08,0x00,0x08,0x00,0x80,0x03,0x84,0x00,0x41,
    0x0b,0x01,0xac,0x02,0x00,0x55,0x03,0x01
};

try
{
    var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(b));
    Console.WriteLine($"OK: subrequests={request.SubRequests.Count}");
    foreach (var sr in request.SubRequests)
    {
        Console.WriteLine($"  RequestId={sr.RequestId} Type={sr.RequestType}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Deserialize failed: {ex}");
}

Console.WriteLine("---- validate proposed generic-skip fix (simulated) ----");
{
    var r = new BinaryReaderEx(b);
    r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt64();

    var requestStart = StreamObjectHeaderStart.Parse(r);
    Console.WriteLine($"Request start type=0x{(int)requestStart.Type:X} len={requestStart.Length} compound={requestStart.Compound}");
    if (requestStart.Length > 0) r.Skip(requestStart.Length);

    // UserAgent
    var userAgentStart = StreamObjectHeaderStart.Parse(r);
    Console.WriteLine($"UserAgent start type=0x{(int)userAgentStart.Type:X} len={userAgentStart.Length} compound={userAgentStart.Compound}");
    if (userAgentStart.Length > 0) r.Skip(userAgentStart.Length);

    Guid? guid = null;
    uint? version = null;
    while (!PeekIsEnd(r))
    {
        var h = StreamObjectHeaderStart.Parse(r);
        if (h.Type == StreamObjectTypeHeaderStart.UserAgentGUID && h.Length == 16)
        {
            byte[] guidBytes = new byte[16];
            for (int i = 0; i < 16; i++) guidBytes[i] = r.ReadByte();
            guid = new Guid(guidBytes);
            Console.WriteLine($"  captured UserAgentGuid={guid}");
        }
        else if (h.Type == StreamObjectTypeHeaderStart.UserAgentVersion && h.Length == 4)
        {
            version = r.ReadUInt32();
            Console.WriteLine($"  captured UserAgentVersion={version}");
        }
        else
        {
            Console.WriteLine($"  skipping unknown UserAgent child type=0x{(int)h.Type:X} len={h.Length}");
            SkipUnknownObject(r, h);
        }
    }
    var userAgentEnd = StreamObjectHeaderEnd.Parse(r);
    Console.WriteLine($"UserAgent end type=0x{(int)userAgentEnd.Type:X}");

    var subRequests = new List<(ulong id, ulong type)>();
    while (r.Remaining > 0 && !PeekIsEnd(r))
    {
        int pos = r.Position;
        var header = StreamObjectHeaderStart.Parse(r);
        if (header.Type == StreamObjectTypeHeaderStart.SubRequest)
        {
            // Parse subrequest preamble.
            var requestId = Compact64bitInt.Deserialize(r).Value;
            var requestType = Compact64bitInt.Deserialize(r).Value;
            var priority = Compact64bitInt.Deserialize(r).Value;
            Console.WriteLine($"  SubRequest RequestId={requestId} Type={requestType} Priority={priority}");

            // Known-type-specific header (leaf, not compound) - just parse+skip its own declared length generically here for validation.
            var innerHeader = StreamObjectHeaderStart.Parse(r);
            Console.WriteLine($"    inner request header type=0x{(int)innerHeader.Type:X} len={innerHeader.Length}");
            if (innerHeader.Length > 0) r.Skip(innerHeader.Length);

            // Consume remaining fields until SubRequest end.
            while (!PeekIsEnd(r))
            {
                var h2 = StreamObjectHeaderStart.Parse(r);
                Console.WriteLine($"    skipping subrequest child type=0x{(int)h2.Type:X} len={h2.Length} compound={h2.Compound}");
                SkipUnknownObject(r, h2);
            }
            var subEnd = StreamObjectHeaderEnd.Parse(r);
            Console.WriteLine($"  SubRequest end type=0x{(int)subEnd.Type:X}");
            subRequests.Add((requestId, requestType));
        }
        else
        {
            Console.WriteLine($"  skipping unknown Request-level child type=0x{(int)header.Type:X} len={header.Length}");
            SkipUnknownObject(r, header);
        }
    }

    var requestEnd = StreamObjectHeaderEnd.Parse(r);
    Console.WriteLine($"Request end type=0x{(int)requestEnd.Type:X}");
    Console.WriteLine($"Final position={r.Position} of {b.Length}");
    Console.WriteLine($"SubRequests parsed: {subRequests.Count}");
}

static bool PeekIsEnd(BinaryReaderEx reader)
{
    int pos = reader.Position;
    byte v = reader.ReadByte();
    reader.Position = pos;
    int disc = v & 0x3;
    return disc == 1 || disc == 3;
}

static void SkipUnknownObject(BinaryReaderEx reader, StreamObjectHeaderStart header)
{
    if (header.Length > 0)
    {
        reader.Skip(header.Length);
    }

    if (header.Compound == 1)
    {
        var expectedEnd = (StreamObjectTypeHeaderEnd)(int)header.Type;
        while (true)
        {
            if (PeekIsEnd(reader))
            {
                var end = StreamObjectHeaderEnd.Parse(reader);
                if (end.Type != expectedEnd)
                {
                    throw new InvalidDataException($"Expected {expectedEnd} end header, got {end.Type}.");
                }
                return;
            }

            var childHeader = StreamObjectHeaderStart.Parse(reader);
            SkipUnknownObject(reader, childHeader);
        }
    }
}

Console.WriteLine("---- decode subrequest prefix ----");
{
    byte[] prefix = new byte[] { 0x03, 0x05, 0x00 };
    var pr = new BinaryReaderEx(prefix);
    var requestId = Compact64bitInt.Deserialize(pr);
    var requestType = Compact64bitInt.Deserialize(pr);
    var priority = Compact64bitInt.Deserialize(pr);
    Console.WriteLine($"RequestId={requestId.Value} RequestType={requestType.Value} Priority={priority.Value} consumed={pr.Position} of {prefix.Length}");
}

Console.WriteLine("---- generic recursive skip validation ----");
{
    var r = new BinaryReaderEx(b);
    r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt64();
    var requestStart = StreamObjectHeaderStart.Parse(r);
    Console.WriteLine($"Request start type=0x{(int)requestStart.Type:X} compound={requestStart.Compound}");
    SkipCompoundBody(r, (int)requestStart.Type, 0);
    Console.WriteLine($"Final position={r.Position}, buffer length={b.Length}");
}

static void SkipCompoundBody(BinaryReaderEx reader, int expectedEndType, int depth)
{
    string pad = new string(' ', depth * 2);
    while (true)
    {
        int pos = reader.Position;
        // Peek discriminator without a dedicated Peek API: read then rewind.
        reader.Position = pos;
        byte peek = ReadByteAt(reader);
        reader.Position = pos;
        int disc = peek & 0x3;
        if (disc == 1 || disc == 3)
        {
            var end = StreamObjectHeaderEnd.Parse(reader);
            Console.WriteLine($"{pad}pos={pos} END type=0x{(int)end.Type:X}");
            if ((int)end.Type != expectedEndType)
            {
                throw new InvalidDataException($"Expected end type 0x{expectedEndType:X}, got 0x{(int)end.Type:X} at {pos}.");
            }
            return;
        }

        var start = StreamObjectHeaderStart.Parse(reader);
        Console.WriteLine($"{pad}pos={pos} START type=0x{(int)start.Type:X} len={start.Length} compound={start.Compound}");
        if (start.Length > 0)
        {
            reader.Skip(start.Length);
        }
        if (start.Compound == 1)
        {
            SkipCompoundBody(reader, (int)start.Type, depth + 1);
        }
    }
}

static byte ReadByteAt(BinaryReaderEx reader)
{
    byte v = reader.ReadByte();
    reader.Position -= 1;
    return v;
}

Console.WriteLine("---- manual walk ----");
var reader = new BinaryReaderEx(b);
Console.WriteLine($"ProtocolVersion={reader.ReadUInt16()}");
Console.WriteLine($"MinimumVersion={reader.ReadUInt16()}");
Console.WriteLine($"Signature=0x{reader.ReadUInt64():X16}");

// Now generically walk headers using the real Parse code, treating every
// "Start" as: parse header, if Compound==0 skip Length bytes as leaf payload
// print it; if Compound==1 just print (no payload skip - the length field for
// compound objects is reserved/0 in this protocol, children follow immediately).
int depth = 0;
int iterations = 0;
while (reader.Remaining > 0 && iterations++ < 100)
{
    int pos = reader.Position;
    byte peek = b[pos];
    int discRaw = peek & 0x3;
    try
    {
        if (discRaw == 0 || discRaw == 2)
        {
            var start = StreamObjectHeaderStart.Parse(reader);
            Console.WriteLine($"{new string(' ', depth * 2)}pos={pos} START type=0x{(int)start.Type:X} len={start.Length} compound={start.Compound} headerSize={start.HeaderSize}");
            if (start.Compound == 0 && start.Length > 0)
            {
                int payloadStart = reader.Position;
                byte[] payload = new byte[start.Length];
                Array.Copy(b, payloadStart, payload, 0, Math.Min(start.Length, b.Length - payloadStart));
                Console.WriteLine($"{new string(' ', depth * 2)}  payload: {Convert.ToHexString(payload)}");
                reader.Skip(start.Length);
            }
            else if (start.Compound == 1)
            {
                depth++;
            }
        }
        else
        {
            var end = StreamObjectHeaderEnd.Parse(reader);
            depth = Math.Max(0, depth - 1);
            Console.WriteLine($"{new string(' ', depth * 2)}pos={pos} END type=0x{(int)end.Type:X}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"pos={pos} ERROR: {ex.Message}");
        break;
    }
}
