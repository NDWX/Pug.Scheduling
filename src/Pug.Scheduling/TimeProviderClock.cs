using Pug.Scheduling.Data.Abstractions;

namespace Pug.Scheduling
{
	// A host or test clock in place of the database's. The session is not consulted. Several
	// coordinator hosts on this clock judge leases by their own clocks, so their skew must stay
	// well below ExecutionCoordinatorOptions.LeaseDuration (see ISchedulingClock).
	public sealed class TimeProviderClock : ISchedulingClock
	{
		private readonly TimeProvider _timeProvider;

		public TimeProviderClock( TimeProvider timeProvider )
		{
			_timeProvider = timeProvider ?? throw new ArgumentNullException( nameof(timeProvider) );
		}

		public Task<DateTime> GetUtcNowAsync( IDataSession session ) =>
			Task.FromResult( _timeProvider.GetUtcNow().UtcDateTime );
	}
}
