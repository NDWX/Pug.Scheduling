using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Abstractions;

namespace Pug.Scheduling
{
	public static class ResourceQualifiers
	{
		public static NounQualifier AnySchedule( string domain ) => new NounQualifier
		{
			Domain = domain,
			Type = SecurityObjectTypes.Schedule
		};

		public static NounQualifier Schedule( string domain, string identifier ) => new NounQualifier
		{
			Domain = domain,
			Type = SecurityObjectTypes.Schedule,
			Identifier = identifier
		};

		// The owner principal a schedule is registered for, in the schedule's domain: owner types
		// map directly onto security object types.
		public static NounQualifier Principal( string domain, Reference principal ) => new NounQualifier
		{
			Domain = domain,
			Type = principal.Type,
			Identifier = principal.Identifier
		};

		// Execution is not domain-scoped: a worker serves every domain the scheduler hosts, so
		// coordinator grants are qualified by the claimant identity being acted as.
		public static NounQualifier ExecutionCoordinator( string claimant ) => new NounQualifier
		{
			Type = SecurityObjectTypes.ExecutionCoordinator,
			Identifier = claimant
		};
	}
}
