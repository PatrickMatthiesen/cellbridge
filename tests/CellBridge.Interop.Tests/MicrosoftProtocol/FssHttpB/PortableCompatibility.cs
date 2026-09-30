namespace Microsoft.Protocols.TestSuites.Common
{
    /// <summary>
    /// Small compatibility surface for the Interop-TestSuites stack. PTF uses
    /// these values for capture and requirement reporting, neither of which is
    /// needed by this independent parser.
    /// </summary>
    public sealed class SharedContext
    {
        public static SharedContext Current { get; } = new();
        public bool IsMsFsshttpRequirementsCaptured => false;
        public object? Site => null;
    }

    public static class AdapterHelper
    {
        public static bool ByteArrayEquals(byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right);
    }
}

namespace Microsoft.Protocols.TestSuites.SharedAdapter
{
    internal static class AdapterHelper
    {
        public static bool ByteArrayEquals(byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right);
    }


internal sealed class MsfsshttpbAdapterCapture
{
    public void InvokeCaptureMethod(Type type, object value, object? site) { }
}

internal static class MsfsshttpbSubRequestMapping
{
    public static void Add(int requestId, Type requestType, object? site) { }
}

}
