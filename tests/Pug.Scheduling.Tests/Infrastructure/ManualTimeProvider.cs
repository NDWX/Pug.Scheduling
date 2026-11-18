namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// A clock that stands still until advanced, for asserting that a facade takes its time from the
/// injected clock (a <see cref="TimeProviderClock"/> over this) rather than the system's or the
/// database's.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
	private DateTimeOffset _now;

	public ManualTimeProvider( DateTime utcNow )
	{
		_now = new DateTimeOffset( DateTime.SpecifyKind( utcNow, DateTimeKind.Utc ) );
	}

	public override DateTimeOffset GetUtcNow() => _now;

	// Timestamps stand still with the clock too, so elapsed-time measurements (GetElapsedTime) move
	// only when advanced.
	public override long GetTimestamp() => _now.UtcTicks;

	public override long TimestampFrequency => TimeSpan.TicksPerSecond;

	public void Advance( TimeSpan by ) => _now += by;
}
