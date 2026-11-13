using System;

namespace Pug.Scheduling.Abstractions
{
	// Thrown by IExecutionCoordinator.ReportCompletionAsync when the reporting worker cannot be
	// verified to hold the claim on the run: the run is recorded under another worker (it was
	// adopted after the claim lapsed), was never claimed by the reporter, or already finished.
	public class ClaimNotHeldException : Exception
	{
		public string Run { get; }

		public ClaimNotHeldException( string run )
			: base( $"The reporting worker does not hold the claim on run '{run}'" )
		{
			Run = run;
		}
	}
}
