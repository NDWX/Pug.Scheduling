using System;

namespace Pug.Scheduling.Abstractions
{
	// Thrown by IExecutionCoordinator operations that reference a run identifier that does not
	// exist — including runs of schedules deleted while executing, whose history is removed with
	// the schedule.
	public class UnknownRunException : Exception
	{
		public string Identifier { get; }

		public UnknownRunException( string identifier )
			: base( $"Unknown run '{identifier}'" )
		{
			Identifier = identifier;
		}
	}
}
