namespace Pug.Scheduling.Abstractions
{
	// Operations checked against SecurityObjectTypes.SchedulerWorker, one per ISchedulerWorker
	// capability. Unlike schedule operations these have no owner exemption — worker management is
	// an operator concern, so every caller requires an explicit grant.
	public static class WorkerSecurityOperations
	{
		public static string GetInfo => "GET_INFO";

		public static string Pause => "PAUSE";

		public static string Resume => "RESUME";
	}
}
