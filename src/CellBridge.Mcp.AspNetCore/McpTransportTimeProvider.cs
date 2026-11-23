namespace CellBridge.Mcp.AspNetCore;

internal sealed class McpTransportTimeProvider(TimeProvider clock) : TimeProvider
{
    public const long Frequency = 1000;
    public override long TimestampFrequency => Frequency;

    // SDK 1.3.0 multiplies TimeSpan ticks by TimestampFrequency in a long
    // before dividing. A millisecond clock avoids overflow at normal idle timeouts.
    public override long GetTimestamp() =>
        (long)((decimal)clock.GetTimestamp() * Frequency / clock.TimestampFrequency);

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        clock.CreateTimer(callback, state, dueTime, period);
}
