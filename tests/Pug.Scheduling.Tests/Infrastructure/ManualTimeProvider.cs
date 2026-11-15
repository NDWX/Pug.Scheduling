namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// A clock that stands still until advanced, for asserting that a facade takes its time from the
/// injected <see cref="TimeProvider"/> rather than the system clock.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
	private DateTimeOffset _now;

	public ManualTimeProvider( DateTime utcNow )
	{
		_now = new DateTimeOffset( DateTime.SpecifyKind( utcNow, DateTimeKind.Utc ) );
	}

	public override DateTimeOffset GetUtcNow() => _now;

	public void Advance( TimeSpan by ) => _now += by;
}
