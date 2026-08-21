namespace Pug.Scheduling.Abstractions
{
	// Object types under which scheduling ACLs are registered in the authorization store.
	public static class SecurityObjectTypes
	{
		// Qualified by the schedule's domain (and, for schedule-specific grants, its identifier).
		public static string Schedule => "SCHEDULE";

		// Worker instances are not domain-scoped: management grants are qualified by the worker's
		// claimant identity only.
		public static string SchedulerWorker => "SCHEDULER_WORKER";

		// Grants an out-of-process worker's service principal the right to act as a given
		// claimant identity against the execution coordinator; qualified by that claimant.
		public static string ExecutionCoordinator => "EXECUTION_COORDINATOR";
	}
}
