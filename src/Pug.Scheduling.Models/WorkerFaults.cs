namespace Pug.Scheduling.Models
{
	// Well-known values of WorkerInfo.Fault: why the coordinator is currently refusing a worker. A
	// fault never pauses the worker — it keeps retrying, and the fault clears by itself once the
	// coordinator answers again (D29).
	public static class WorkerFaults
	{
		// The coordinator refuses the worker's claims (NotAuthorized): its claimant lacks CLAIM_JOBS,
		// or the coordinator's security provider is failing closed. Set from the first refused claim
		// until a claim is answered, with a job or with none; meanwhile the worker polls at
		// WorkerOptions.RefusedPollRetryInterval.
		public const string ClaimRefused = "CLAIM_REFUSED";
	}
}
