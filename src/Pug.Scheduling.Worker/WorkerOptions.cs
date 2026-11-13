namespace Pug.Scheduling.Worker
{
	// Configuration of one worker group: Workers identical workers that claim under one claimant
	// identity, each holding its claims as "Claimant/{its host-wide worker number}".
	public record WorkerOptions
	{
		// Identity of the worker group: the claimant its grants are qualified by. Must not be blank
		// or contain '/'. Defaults to the machine name, which is stable across restarts: a
		// restarted worker presenting the same claimant and number re-takes its own lease and
		// adopts its unfinished run. Two worker processes on one machine must therefore set
		// distinct claimants.
		public string Claimant { get; init; } = Environment.MachineName;

		// Number of workers in the group, each claiming and executing one job at a time.
		public int Workers { get; init; } = 1;

		// The only job type the group's workers claim; null claims any type.
		public string JobType { get; init; }

		// How long to wait between polls when no work was claimed.
		public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds( 10 );

		// Claim-renewal cadence during job execution. Must be comfortably shorter than the
		// coordinator's lease duration, or claims lapse mid-run and peers re-execute.
		public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds( 30 );

		/// <summary>
		/// Throws <see cref="ArgumentException"/> (naming the offending property) unless
		/// <see cref="Claimant"/> is neither blank nor contains '/', <see cref="Workers"/> is at
		/// least 1, and <see cref="JobType"/> is null or not blank.
		/// </summary>
		public void Validate()
		{
			if( string.IsNullOrWhiteSpace( Claimant ) )
				throw new ArgumentException( "Worker claimant is required.", nameof(Claimant) );

			if( Claimant.Contains( '/' ) )
				throw new ArgumentException( "Worker claimant must not contain '/'.", nameof(Claimant) );

			if( Workers < 1 )
				throw new ArgumentException( "A worker group must have at least one worker.", nameof(Workers) );

			if( JobType is not null && string.IsNullOrWhiteSpace( JobType ) )
				throw new ArgumentException( "Worker job type must be null (any type) or not blank.", nameof(JobType) );
		}
	}
}
