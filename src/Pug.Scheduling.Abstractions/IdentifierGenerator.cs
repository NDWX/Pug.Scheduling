namespace Pug.Scheduling.Abstractions
{
	// Generates unique identifiers for schedules and runs. Supplied by the host (per
	// Pug.Corea.Abstractions.IdentifierGenerator precedent).
	public interface IdentifierGenerator
	{
		string GetNext();
	}
}
