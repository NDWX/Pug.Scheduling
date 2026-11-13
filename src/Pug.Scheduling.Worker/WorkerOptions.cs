namespace Pug.Scheduling.Worker
{
	public record WorkerOptions
	{
		// Identity of this worker instance, used as the claimant its grants are qualified by (the
		// coordinator holds its leases as "Claimant/1"); must not contain '/'. Defaults to the
		// machine name plus a random suffix so multiple instances per machine stay distinct.
		public string Claimant { get; init; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";

		// How long to wait between polls when no work was claimed.
		public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds( 10 );

		// Claim-renewal cadence during job execution. Must be comfortably shorter than the
		// coordinator's lease duration, or claims lapse mid-run and peers re-execute.
		public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds( 30 );
	}
}
