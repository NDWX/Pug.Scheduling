namespace Pug.Scheduling.Abstractions
{
	// Operations checked against SecurityObjectTypes.ExecutionCoordinator, one per
	// IExecutionCoordinator capability. IExecutionCoordinator performs no authorization itself —
	// an in-process worker is inside the trust boundary — so these are enforced by the coordinator
	// endpoints, the boundary an externally hosted worker crosses. Qualifying grants by claimant
	// keeps one worker's credentials from claiming or completing runs as another.
	public static class CoordinatorSecurityOperations
	{
		public static string ClaimJobs => "CLAIM_JOBS";

		public static string RenewClaim => "RENEW_CLAIM";

		public static string ReportCompletion => "REPORT_COMPLETION";
	}
}
