#if OFFICE_INSPECTORS
using System.Collections.Generic;
using FSSHTTPandWOPIInspector.Parsers;

namespace FSSHTTPandWOPIInspector;

// The parser source reads these fields from its Fiddler inspector host. The
// adapter does not need the UI or Fiddler itself, so keep only that state.
public static class FSSHTTPandWOPIInspector
{
    public static bool IsOneStore;
    public static readonly List<ExtendedGUID> encryptedObjectGroupIDList = new();
    public static bool isNextEditorTable;
}
#endif
