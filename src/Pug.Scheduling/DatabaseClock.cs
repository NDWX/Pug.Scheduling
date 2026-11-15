using Pug.Scheduling.Data.Abstractions;

namespace Pug.Scheduling
{
	// The database's clock (IDataSession.GetCurrentTimeAsync): the default ISchedulingClock.
	public sealed class DatabaseClock : ISchedulingClock
	{
		public Task<DateTime> GetUtcNowAsync( IDataSession session )
		{
			if( session is null ) throw new ArgumentNullException( nameof(session) );

			return session.GetCurrentTimeAsync();
		}
	}
}
