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
	}
}
