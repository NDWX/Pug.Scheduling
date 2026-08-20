namespace Pug.Scheduling.Abstractions
{
	// Operations checked against SecurityObjectTypes.Schedule, one per IScheduler capability.
	// The definition's Owner principal is exempt: owners may always perform these operations on
	// their own schedules without an ACL grant.
	public static class SecurityOperations
	{
		public static string Create => "CREATE";

		public static string List => "LIST";

		public static string GetInfo => "GET_INFO";

		public static string SetDefinition => "SET_DEFINITION";

		public static string Delete => "DELETE";

		public static string Pause => "PAUSE";

		public static string Resume => "RESUME";

		public static string RunNow => "RUN_NOW";

		public static string GetRunHistory => "GET_RUN_HISTORY";
	}
}
