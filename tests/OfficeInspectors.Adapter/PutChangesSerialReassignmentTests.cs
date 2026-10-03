using CellBridge.OfficeInspectors;
using CellBridge.OfficeInspectors.Parsers;
using Server = CellBridge.FssHttpB;

namespace OfficeInspectors.Adapter;

public sealed class PutChangesSerialReassignmentTests
{
    private static readonly Guid Identity = new("10000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReassignmentsPreserveElementIdsAndUnsignedSerials(bool fullDefaultSerial, bool sharePointProfile)
    {
        var prefix = Prefix(fullDefaultSerial, entries: true);
        var bytes = Response(prefix, sharePointProfile);
        var result = OfficeInspector.ParseResponse(bytes);
        Assert.True(result.Parsed, result.Error);
        var put = Assert.IsType<PutChangesResponse>(Assert.Single(result.Response.SubResponses).SubResponseData);
        if (fullDefaultSerial)
            Assert.Equal(17UL, Assert.IsType<SerialNumber64BitUintValue>(put.SerialNumberReassignAll.SerialNumber).Value);
        else
            Assert.IsType<SerialNumberNullValue>(put.SerialNumberReassignAll.SerialNumber);
        Assert.Equal(2, put.SerialNumberReassignments.Length);
        for (int i = 0; i < 2; i++)
        {
            var entry = put.SerialNumberReassignments[i];
            Assert.Equal(Identity, entry.DataElementId.GetGUID(entry.DataElementId));
            Assert.Equal((uint)(i + 1), entry.DataElementId.GetValue(entry.DataElementId));
            var serial = Assert.IsType<SerialNumber64BitUintValue>(entry.SerialNumber);
            Assert.Equal(Identity, serial.GUID);
            Assert.Equal(i == 0 ? 18UL : ulong.MaxValue, serial.Value);
        }
        Assert.NotNull(put.ResultantKnowledge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefixAndAppliedIndexAreOptional(bool prefixPresent)
    {
        var result = OfficeInspector.ParseResponse(Response(prefixPresent ? Prefix(false, false) : null));
        Assert.True(result.Parsed, result.Error);
        var put = Assert.IsType<PutChangesResponse>(Assert.Single(result.Response.SubResponses).SubResponseData);
        Assert.Equal(prefixPresent, put.SerialNumberReassignAll is not null);
        Assert.Empty(put.SerialNumberReassignments);
        Assert.NotNull(put.ResultantKnowledge);
    }

    [Fact]
    public void MalformedLengthsCompoundRecordsAndUnknownSerialsAreRejected()
    {
        var valid = Prefix(false, true);
        var wrongDefaultLength = valid.ToArray();
        wrongDefaultLength[2] = 4; // Declares two bytes for the one-byte null serial.
        var compound = valid.ToArray();
        compound[0] |= 4;
        var unknownSerial = valid.ToArray();
        unknownSerial[4] = 0x40;
        var wrongEntryLength = valid.ToArray();
        wrongEntryLength[7] -= 2; // First entry payload is one byte longer than declared.
        var orphanEntry = valid[5..];
        foreach (var prefix in new[] { wrongDefaultLength, compound, unknownSerial, wrongEntryLength, orphanEntry })
            Assert.False(OfficeInspector.ParseResponse(Response(prefix)).Parsed);
    }

    [Fact]
    public void TruncatedReassignmentsAndMissingKnowledgeAreRejected()
    {
        var bytes = Response(Prefix(false, true));
        Assert.True(OfficeInspector.ParseResponse(bytes).Parsed);
        for (int length = 0; length < bytes.Length; length++)
            Assert.False(OfficeInspector.ParseResponse(bytes[..length]).Parsed, $"Accepted length {length}.");
        Assert.False(OfficeInspector.ParseResponse(Response(Prefix(false, false), knowledge: [])).Parsed);
    }

    [Theory]
    [InlineData(0x1_0000_0001UL)]
    [InlineData(ulong.MaxValue)]
    public void ExtendedLengthsCannotWrapIntoAValidPayloadLength(ulong length)
    {
        var prefix = new Server.BinaryWriterEx();
        prefix.WriteBytes([0x2a, 0x02, 0xfe, 0xff]); // 0x045, extended length.
        new Server.Compact64bitInt(length).Serialize(prefix);
        Server.SerialNumber.Null.Serialize(prefix);
        Assert.False(OfficeInspector.ParseResponse(Response(prefix.ToArray())).Parsed);
    }

    private static byte[] Prefix(bool fullDefaultSerial, bool entries)
    {
        var writer = new Server.BinaryWriterEx();
        var serial = fullDefaultSerial ? new Server.SerialNumber(Identity, 17) : Server.SerialNumber.Null;
        new Server.StreamObjectHeaderStart32Bit(Server.StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassignAll,
            fullDefaultSerial ? 25 : 1).Serialize(writer);
        serial.Serialize(writer);
        if (entries)
            for (int i = 0; i < 2; i++)
            {
                var body = new Server.BinaryWriterEx();
                new Server.ExGuid((uint)(i + 1), Identity).Serialize(body);
                new Server.SerialNumber(Identity, i == 0 ? 18UL : ulong.MaxValue).Serialize(body);
                new Server.StreamObjectHeaderStart32Bit(Server.StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign,
                    body.Length).Serialize(writer);
                writer.WriteBytes(body.ToArray());
            }
        return writer.ToArray();
    }

    private static byte[] Response(byte[]? prefix, bool sharePointProfile = true, byte[]? knowledge = null)
    {
        var response = new Server.FsshttpbResponse();
        response.SubResponses.Add(new Server.FsshttpbSubResponse
        {
            RequestId = 1,
            RequestType = Server.RequestTypes.PutChanges,
            Data = new Server.PutChangesSubResponseData
            {
                SerialNumberReassignAllBytes = prefix,
                KnowledgeBytes = knowledge ?? Server.BinaryKnowledgeBuilder.FromElements([], Server.ExGuid.Null, 0),
            },
        });
        if (knowledge is { Length: 0 })
        {
            // The server serializer rejects missing Knowledge. Remove the known
            // framed object from a valid message to exercise the diagnostic parser.
            var valid = Response(prefix, sharePointProfile);
            var known = Server.BinaryKnowledgeBuilder.FromElements([], Server.ExGuid.Null, 0);
            int start = valid.AsSpan().IndexOf(known);
            return [..valid[..start], ..valid[(start + known.Length)..]];
        }
        return response.ToByteArray(sharePointProfile
            ? Server.FsshttpbSerializationProfile.SharePoint13_11
            : Server.FsshttpbSerializationProfile.Current);
    }
}
