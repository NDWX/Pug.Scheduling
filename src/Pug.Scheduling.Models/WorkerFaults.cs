namespace Pug.Scheduling.Models
{
	// Well-known values of WorkerInfo.Fault: why a worker paused itself. A worker pauses itself only
	// when carrying on would execute work it can neither keep nor record; each fault names the
	// coordinator grant its claimant lacks. Grant it, then resume the worker, which clears the fault.
	public static class WorkerFaults
	{
		// The coordinator refused to renew a claim (NotAuthorized): the claimant lacks RENEW_CLAIM.
		// Every run longer than a lease would otherwise be cancelled at its first renewal and
		// executed again by the next claim.
		public const string RenewalRefused = "RENEWAL_REFUSED";

		// The coordinator refused a completion report (NotAuthorized): the claimant lacks
		// REPORT_COMPLETION. The worker would otherwise adopt and execute the same run again at
		// every poll, without ever using up an attempt.
		public const string CompletionRefused = "COMPLETION_REFUSED";
	}
}
