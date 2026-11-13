namespace Pug.Scheduling.Abstractions
{
	// Operations checked against SecurityObjectTypes.ExecutionCoordinator, one per
	// IExecutionCoordinator capability. The coordinator enforces them itself, qualified by the
	// claimant (worker group) identity being acted as, so an in-process worker is held to the
	// same grants as one calling in over a transport, and one group's credentials cannot claim,
	// renew, or complete runs as another. Calls arriving with no principal at all are in-process
	// by construction and are not checked.
	public static class CoordinatorSecurityOperations
	{
		public static string ClaimJobs => "CLAIM_JOBS";

		public static string RenewClaim => "RENEW_CLAIM";

		public static string ReportCompletion => "REPORT_COMPLETION";
	}
}
