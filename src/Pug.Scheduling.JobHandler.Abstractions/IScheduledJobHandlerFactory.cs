using Pug.Scheduling.Models;

namespace Pug.Scheduling.JobHandler.Abstractions
{
	// Creates the handler for one run. The worker calls Create once per claimed run and disposes
	// the handler afterwards. Third-party code: an exception or null return from Create is
	// reported as an unsuccessful outcome.
	public interface IScheduledJobHandlerFactory
	{
		IScheduledJobHandler Create( ScheduledJobContext context );
	}
}
