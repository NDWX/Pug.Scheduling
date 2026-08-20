using System.Collections.Generic;
using System.Threading.Tasks;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Optional per-job-type validation of schedule definitions at registration and update time,
	// registered against the job type key. The scheduler validates recurrence, time zone, and
	// policies itself; implementations validate what only the job type understands — typically
	// the Payload (e.g. that a referenced report configuration exists and belongs to the
	// definition's domain).
	public interface IJobPayloadValidator
	{
		// Returns validation errors for the definition; an empty result means the definition is
		// acceptable. Registration/update is rejected with these errors when non-empty.
		Task<IEnumerable<string>> ValidateAsync( ScheduleDefinition definition );
	}
}
