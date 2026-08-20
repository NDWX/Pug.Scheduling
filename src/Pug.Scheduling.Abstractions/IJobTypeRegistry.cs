namespace Pug.Scheduling.Abstractions
{
	// Job types schedulable in this deployment, and their optional payload validators. Kept
	// distinct from handler registration because the scheduling API host does not necessarily
	// host the handlers (workers may be external): what is *schedulable* here and what is
	// *executable* where are separate concerns.
	public interface IJobTypeRegistry
	{
		bool IsRegistered( string jobType );

		// The job type's payload validator, or null when the job type does not validate payloads.
		IJobPayloadValidator GetPayloadValidator( string jobType );
	}
}
