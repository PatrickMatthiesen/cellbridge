using System.Security.Cryptography;

namespace CellBridge.FssHttpB;

/// <summary>Independent wire-hash configuration. The secret is never a content-store identity.</summary>
public sealed class ProtocolHashingOptions
{
    private readonly byte[] _serverSecret;
    public bool Enabled { get; }
    internal ReadOnlySpan<byte> ServerSecret => _serverSecret;

    /// <summary>Process-lifetime configuration. Supply a stable secret for stable cache keys across restarts.</summary>
    public static ProtocolHashingOptions Default { get; } = new(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
    public static ProtocolHashingOptions Disabled { get; } = new([], false);

    /// <summary>Accepts a host-configured SHA-256 server secret and takes an owned copy.</summary>
    public ProtocolHashingOptions(ReadOnlySpan<byte> serverSecret, bool enabled = true)
    {
        if (enabled && serverSecret.Length != 32)
            throw new ArgumentException("A SHA-256 server secret must contain 32 bytes.", nameof(serverSecret));
        _serverSecret = serverSecret.ToArray();
        Enabled = enabled;
    }
}
