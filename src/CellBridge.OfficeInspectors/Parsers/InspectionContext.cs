using System.Runtime.CompilerServices;

namespace CellBridge.OfficeInspectors.Parsers;

// All structures decoding the same stream share message state. The weak keys
// let completed messages go without retaining their graphs or serializing parses.
internal sealed class InspectionContext
{
    private static readonly ConditionalWeakTable<Stream, InspectionContext> Contexts = new();

    internal bool IsOneStore { get; set; }
    internal bool IsNextEditorTable { get; set; }
    internal List<ExtendedGUID> EncryptedObjectGroupIds { get; } = [];

    internal static InspectionContext For(Stream stream) => Contexts.GetOrCreateValue(stream);
}
