namespace Campfire.Server.Composition;

/// <summary>
/// A <see cref="TimeProvider"/> that offsets from UTC time by a fixed initial difference,
/// allowing virtual clocks to tick forward at normal speed from a given starting instant.
/// </summary>
public sealed class OffsetClock(DateTimeOffset startedAt, DateTimeOffset virtualStart) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => virtualStart + (DateTimeOffset.UtcNow - startedAt);
}
