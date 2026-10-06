using System.Globalization;
using System.Xml;

namespace CellBridge.FssHttp;

/// <summary>Shared lexical validation for outer Cell subrequest attributes.</summary>
public static class CellSubRequestDataValidation
{
    private static readonly string[] BooleanAttributes =
        ["Coalesce", "GetFileProps", "CoauthVersioning", "ExpectNoFileExists"];
    private static readonly string[] GuidAttributes =
        ["PartitionID", "SchemaLockID", "ExclusiveLockID", "BypassLockID"];

    public static bool TryValidate(IReadOnlyDictionary<string, string> attributes, bool requireBinaryDataSize,
        out string message)
    {
        foreach (string name in BooleanAttributes)
        {
            if (attributes.TryGetValue(name, out string? text) && !TryParseXmlBoolean(text, out _))
            {
                message = $"SubRequestData.{name} must be an XML boolean.";
                return false;
            }
        }
        foreach (string name in GuidAttributes)
        {
            if (attributes.TryGetValue(name, out string? text) && !Guid.TryParse(text, out _))
            {
                message = $"SubRequestData.{name} must be a GUID.";
                return false;
            }
        }

        if (requireBinaryDataSize && !attributes.ContainsKey("BinaryDataSize"))
        {
            message = "Cell SubRequestData.BinaryDataSize is required.";
            return false;
        }
        if (attributes.TryGetValue("BinaryDataSize", out string? sizeText) &&
            (!TryParseXmlInt64(sizeText, out long size) || size < 1))
        {
            message = "Cell SubRequestData.BinaryDataSize must be between 1 and Int64.MaxValue.";
            return false;
        }

        if (attributes.TryGetValue("Timeout", out string? timeoutText) &&
            (!TryParseXmlInt64(timeoutText, out long timeout) || timeout is < 60 or > 120000))
        {
            message = "Cell SubRequestData.Timeout must be between 60 and 120000.";
            return false;
        }
        if (attributes.ContainsKey("ExclusiveLockID") && !attributes.ContainsKey("Timeout"))
        {
            message = "Cell SubRequestData.Timeout is required when ExclusiveLockID is present.";
            return false;
        }

        if (attributes.TryGetValue("LastModifiedTime", out string? lastModified) &&
            !TryParseFileTime(lastModified, out _))
        {
            message = "Cell SubRequestData.LastModifiedTime must be a valid nonnegative UTC FILETIME.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public static bool TryGetBoolean(IReadOnlyDictionary<string, string> attributes, string name, out bool value)
    {
        value = false;
        return attributes.TryGetValue(name, out string? text) && TryParseXmlBoolean(text, out value);
    }

    public static bool TryGetLastModifiedTime(IReadOnlyDictionary<string, string> attributes, out DateTime value)
    {
        value = default;
        return attributes.TryGetValue("LastModifiedTime", out string? text) && TryParseFileTime(text, out value);
    }

    public static bool TryParseXmlBoolean(string text, out bool value)
    {
        try
        {
            value = XmlConvert.ToBoolean(CollapseXmlWhitespace(text));
            return true;
        }
        catch (FormatException)
        {
            value = false;
            return false;
        }
    }

    public static bool TryParseXmlUnsignedInt(string text, out uint value) =>
        uint.TryParse(CollapseXmlWhitespace(text), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);

    public static bool TryParseXmlUnsignedShort(string text, out ushort value)
    {
        value = default;
        if (!TryParseXmlUnsignedInt(text, out uint parsed) || parsed > ushort.MaxValue) return false;
        value = (ushort)parsed;
        return true;
    }

    public static bool TryParseXmlInt64(string text, out long value) =>
        long.TryParse(CollapseXmlWhitespace(text), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);

    private static string CollapseXmlWhitespace(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char character in text)
        {
            if (character is ' ' or '\t' or '\r' or '\n')
            {
                pendingSpace = result.Length != 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static bool TryParseFileTime(string text, out DateTime value)
    {
        value = default;
        if (!TryParseXmlInt64(text, out long fileTime)) return false;
        try { value = DateTime.FromFileTimeUtc(fileTime); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
