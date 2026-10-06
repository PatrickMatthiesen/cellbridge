using Xunit.Abstractions;

namespace CellBridge.FssHttpB.Tests;

public sealed class BoundedProtocolMutationTests(ITestOutputHelper output)
{
    private const int Seed = 0x18_34;

    [Fact]
    public void GeneratedRequestsRejectEveryTruncatedPrefixAndCorruptSignature()
    {
        var random = new Random(Seed);
        for (int sample = 0; sample < 16; sample++)
        {
            var request = new FsshttpbCellRequest { ProtocolVersion = 13,
                UserAgentClientAndPlatform = new("bounded-" + sample, "test"),
                UserAgentVersionValue = (uint)random.Next() };
            int count = random.Next(1, 5);
            for (int i = 0; i < count; i++)
                request.SubRequests.Add(new(RequestTypes.AllocateExtendedGuidRange)
                {
                    RequestId = (ulong)(sample * 4 + i + 1), Priority = (ulong)random.Next(0, 4),
                    TargetPartitionId = Guid.Empty,
                    Data = new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = (ulong)random.Next(1, 1001) },
                });
            byte[] wire = request.ToByteArray();
            Assert.InRange(wire.Length, 1, 1024);
            output.WriteLine($"seed={Seed} sample={sample} bytes={wire.Length} mutation=prefix/signature");
            var reader = new BinaryReaderEx(wire);
            var valid = FsshttpbCellRequest.Deserialize(reader);
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(request.SubRequests.Select(s => s.RequestId), valid.SubRequests.Select(s => s.RequestId));
            for (int length = 0; length < wire.Length; length++)
            {
                var error = Record.Exception(() => FsshttpbCellRequest.Deserialize(new(wire.AsMemory(0, length))));
                Assert.True(error is EndOfStreamException or InvalidDataException,
                    $"seed={Seed} sample={sample} prefix={length}: expected framing failure, got {error}");
            }
            for (int offset = 4; offset < 12; offset++)
            {
                var mutated = wire.ToArray(); mutated[offset] ^= 0x80;
                Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(mutated)));
            }
        }
    }

    [Fact]
    public void UnknownLeafObjectsAreSkippedButTheirMalformedLengthsFail()
    {
        var request = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 1 } } };
        byte[] wire = request.ToByteArray();
        foreach (int length in new[] { 0, 1, 7, 31, 255 })
        {
            var writer = new BinaryWriterEx();
            writer.WriteBytes(wire.AsSpan(0, wire.Length - 2));
            new StreamObjectHeaderStart32Bit((StreamObjectTypeHeaderStart)0x1FE, length).Serialize(writer);
            writer.WriteBytes(new byte[length]);
            writer.WriteBytes(wire.AsSpan(wire.Length - 2));
            var reader = new BinaryReaderEx(writer.ToArray());
            Assert.Equal(1UL, Assert.Single(FsshttpbCellRequest.Deserialize(reader).SubRequests).RequestId);
            Assert.Equal(0, reader.Remaining);
        }
        var malformed = new BinaryWriterEx();
        malformed.WriteBytes(wire.AsSpan(0, wire.Length - 2));
        new StreamObjectHeaderStart32Bit((StreamObjectTypeHeaderStart)0x1FE, 4096).Serialize(malformed);
        malformed.WriteBytes(wire.AsSpan(wire.Length - 2));
        Assert.Throws<EndOfStreamException>(() => FsshttpbCellRequest.Deserialize(new(malformed.ToArray())));
    }

    [Fact]
    public void DuplicateRequestIdsAndOperationPayloadsAreRejectedOnTheWire()
    {
        var duplicateIds = new FsshttpbCellRequest
        {
            SubRequests =
            {
                new(RequestTypes.QueryAccess) { RequestId = 0 },
                new(RequestTypes.QueryAccess) { RequestId = 0 },
            },
        };
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(duplicateIds.ToByteArray())));

        var request = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1, Data = new DuplicateQueryChangesData() } },
        };
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(request.ToByteArray())));
    }

    private sealed class DuplicateQueryChangesData : ISubRequestData
    {
        public void Serialize(BinaryWriterEx writer)
        {
            new QueryChangesSubRequestData().Serialize(writer);
            new QueryChangesSubRequestData().Serialize(writer);
        }
    }

    [Fact]
    public void GeneratedResponsesRejectEveryTruncatedPrefixAndPreserveIgnoredReservedBits()
    {
        for (int sample = 0; sample < 16; sample++)
        {
            var response = new FsshttpbResponse { SubResponses = { new()
            {
                RequestId = (ulong)sample + 1, RequestType = RequestTypes.AllocateExtendedGuidRange,
                Data = new AllocateExtendedGuidRangeSubResponseData { GuidComponent = new("12345678-1234-1234-1234-123456789abc"),
                    IntegerRangeMin = (ulong)sample, IntegerRangeMax = (ulong)sample + 1000 },
            } } };
            byte[] wire = response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);
            output.WriteLine($"seed={Seed} sample={sample} bytes={wire.Length} mutation=response-prefix/reserved");
            for (int offset = 4; offset < 12; offset++)
            {
                var mutated = wire.ToArray(); mutated[offset] ^= 0x80;
                Assert.Throws<InvalidDataException>(() => FsshttpbResponse.Deserialize(new(mutated)));
            }
            for (int length = 0; length < wire.Length; length++)
            {
                var error = Record.Exception(() => FsshttpbResponse.Deserialize(new(wire.AsMemory(0, length))));
                Assert.True(error is EndOfStreamException or InvalidDataException,
                    $"seed={Seed} sample={sample} prefix={length}: expected framing failure, got {error}");
            }
            wire[16] |= 0xFE; // Response status byte: bits 1..7 MUST be ignored on read.
            var reader = new BinaryReaderEx(wire);
            var parsed = FsshttpbResponse.Deserialize(reader);
            Assert.False(parsed.Status); Assert.Equal(0, reader.Remaining);
            Assert.Equal((ulong)sample + 1, Assert.Single(parsed.SubResponses).RequestId);
        }
    }
}
