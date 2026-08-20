using Pug.Application.Security;
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
	}
}
