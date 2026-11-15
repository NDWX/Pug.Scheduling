using Pug.Scheduling.Data.Abstractions;

namespace Pug.Scheduling
{
	// The clock every time-dependent decision of the facades reads: due-ness, misfire, lease
	// expiry, retry and next-occurrence times, and the run and audit timestamps they write.
	//
	// It is read through the unit of work's session, inside the unit and after any row lock the
	// unit takes, so a wait for a peer's lock never leaves "now" stale. DatabaseClock, the default,
	// reads the database's clock, the one clock every coordinator host sharing a store agrees on.
	// TimeProviderClock reads a host or test clock instead: lease expiry and due-ness are then
	// judged by each host's own clock, so it suits tests and single-host deployments only (a host
	// whose clock runs ahead of its peers' treats their live leases as lapsed and adopts the runs
	// while they still execute).
	public interface ISchedulingClock
	{
		// The current UTC time, read for the unit of work that session belongs to.
		Task<DateTime> GetUtcNowAsync( IDataSession session );
	}
}
