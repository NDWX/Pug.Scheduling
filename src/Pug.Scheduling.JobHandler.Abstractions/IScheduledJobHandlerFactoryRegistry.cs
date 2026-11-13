namespace Pug.Scheduling.JobHandler.Abstractions
{
	// Maps a job type to the factory that creates its handlers.
	public interface IScheduledJobHandlerFactoryRegistry
	{
		// Returns the factory registered for the job type, or null when there is none.
		IScheduledJobHandlerFactory GetFactory( string jobType );
	}
}
