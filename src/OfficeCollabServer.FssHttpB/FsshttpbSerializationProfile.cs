namespace OfficeCollabServer.FssHttpB;

/// <summary>Selects the wire framing used by FSSHTTPB response serialization.</summary>
public enum FsshttpbSerializationProfile
{
    /// <summary>The current server framing (version 12/11).</summary>
    Current = 0,

    /// <summary>
    /// SharePoint's Word-compatible 13/11 framing: 16-bit starts and 8-bit
    /// ends for the data-element package and its data elements.
    /// </summary>
    SharePoint13_11 = 1,
}

internal static class FsshttpbSerializationProfileExtensions
{
    public static ushort ProtocolVersion(this FsshttpbSerializationProfile profile) => profile switch
    {
        FsshttpbSerializationProfile.Current => FsshttpbCellRequest.CurrentProtocolVersion,
        FsshttpbSerializationProfile.SharePoint13_11 => 13,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown FSSHTTPB serialization profile."),
    };

    public static ushort MinimumVersion(this FsshttpbSerializationProfile profile) => profile switch
    {
        FsshttpbSerializationProfile.Current => FsshttpbCellRequest.MinimumProtocolVersion,
        FsshttpbSerializationProfile.SharePoint13_11 => 11,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown FSSHTTPB serialization profile."),
    };

    public static bool UsesSharePointLegacyDataElementFraming(this FsshttpbSerializationProfile profile) => profile switch
    {
        FsshttpbSerializationProfile.Current => false,
        FsshttpbSerializationProfile.SharePoint13_11 => true,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown FSSHTTPB serialization profile."),
    };
}
