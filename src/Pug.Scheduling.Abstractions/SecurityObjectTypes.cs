namespace Pug.Scheduling.Abstractions
{
	// Object types under which schedule ACLs are registered in the authorization store, qualified
	// by the schedule's domain.
	public static class SecurityObjectTypes
	{
		public static string Schedule => "SCHEDULE";
	}
}
