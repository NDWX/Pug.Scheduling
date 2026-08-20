using System;

namespace Pug.Scheduling.Abstractions
{
	// Thrown by IScheduler operations that reference a schedule identifier that does not exist
	// (or is not visible within the caller's domain).
	public class UnknownScheduleException : Exception
	{
		public string Identifier { get; }

		public UnknownScheduleException( string identifier )
			: base( $"Unknown schedule '{identifier}'" )
		{
			Identifier = identifier;
		}
	}
}
