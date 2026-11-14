using System;

namespace Pug.Scheduling.Abstractions
{
	// Thrown by IScheduler.RunNowAsync when the schedule has a run in progress: held by a worker,
	// or lapsed and awaiting adoption. The request is refused rather than queued, so it can never
	// move the schedule's occurrence out from under the run (409 over HTTP). Run, Holder and
	// LeaseExpiry describe the run in progress when known; a remote client that receives only the
	// status leaves them null.
	public class RunInProgressException : Exception
	{
		public string Schedule { get; }

		public string Run { get; }

		public string Holder { get; }

		public DateTime? LeaseExpiry { get; }

		public RunInProgressException( string schedule, string run = null, string holder = null,
										DateTime? leaseExpiry = null )
			: base( $"Schedule '{schedule}' has a run in progress" )
		{
			Schedule = schedule;
			Run = run;
			Holder = holder;
			LeaseExpiry = leaseExpiry;
		}
	}
}
