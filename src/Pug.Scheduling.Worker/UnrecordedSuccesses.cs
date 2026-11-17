using Pug.Scheduling.Models;

namespace Pug.Scheduling.Worker
{
	/// <summary>
	/// The successful outcomes a worker could not get recorded, kept so that a later claim of the
	/// same occurrence replays the outcome instead of running the handler again (D29). Keyed by
	/// occurrence — <c>( Schedule, ScheduledFor )</c>, the domain's idempotency key — not by run: a
	/// retry after a peer's failure is a new run of the same occurrence, and may come back here.
	///
	/// Bounds, and why they suffice: an occurrence can be claimed again only while a run of it is
	/// active, which is normally minutes, so entries expire after <see cref="Retention"/> and at most
	/// <see cref="Capacity"/> are kept, the oldest evicted first. Past either bound the job's own
	/// idempotency on <c>( Schedule, ScheduledFor )</c> is the backstop, as it always is.
	///
	/// A replay re-sends the original outcome, <see cref="JobOutcome.Details"/> included: it is the
	/// true result of the work that was done, not a new one.
	///
	/// Limits: in memory, so a restart forgets everything; per host, so an occurrence adopted by a
	/// worker of another host runs again; and per coordinator instance (one store per coordinator
	/// the host's worker groups use), since a group bound to another scheduler may see colliding
	/// schedule identifiers.
	///
	/// Thread-safe: one lock guards the entries and their insertion order. Every call first evicts
	/// what has expired or overflowed, so the bounds hold without a timer.
	/// </summary>
	public sealed class UnrecordedSuccesses
	{
		/// <summary>The most entries kept; beyond it the oldest is evicted.</summary>
		public const int Capacity = 1000;

		/// <summary>How long an entry is kept after it was first remembered.</summary>
		public static readonly TimeSpan Retention = TimeSpan.FromHours( 24 );

		private readonly TimeProvider _timeProvider;
		private readonly object _lock = new();
		private readonly Dictionary<Occurrence, Entry> _entries = new();

		// Keys in the order they were first remembered, oldest first, so both bounds evict from the
		// head.
		private readonly LinkedList<Occurrence> _order = new();

		/// <param name="timeProvider">
		/// Times <see cref="Retention"/> (its <c>GetTimestamp</c>); null is <see cref="TimeProvider.System"/>.
		/// </param>
		public UnrecordedSuccesses( TimeProvider timeProvider = null )
		{
			_timeProvider = timeProvider ?? TimeProvider.System;
		}

		/// <summary>
		/// Remembers a successful outcome of the context's occurrence. Remembering an occurrence
		/// already held replaces its outcome and definition but keeps its original time and place,
		/// so a success that keeps failing to be recorded still expires a <see cref="Retention"/>
		/// after it first was.
		/// </summary>
		public void Remember( ScheduledJobContext context, JobOutcome outcome )
		{
			if( context is null ) throw new ArgumentNullException( nameof(context) );
			if( outcome is null ) throw new ArgumentNullException( nameof(outcome) );

			Occurrence key = Occurrence.Of( context );

			lock( _lock )
			{
				EvictExpired();

				if( _entries.TryGetValue( key, out Entry existing ) )
				{
					_entries[key] = existing with { Outcome = outcome, JobType = context.JobType, Payload = context.Payload };

					return;
				}

				_entries[key] = new Entry( outcome, context.JobType, context.Payload, _timeProvider.GetTimestamp(),
											_order.AddLast( key ) );

				while( _entries.Count > Capacity )
					Remove( _order.First.Value );
			}
		}

		/// <summary>
		/// Gets the remembered outcome of the context's occurrence. An entry remembered under another
		/// job type or payload — the definition changed before the occurrence came back — is not
		/// replayed: it is removed, <paramref name="definitionChanged"/> is true, and the caller runs
		/// the job.
		/// </summary>
		public bool TryGet( ScheduledJobContext context, out JobOutcome outcome, out bool definitionChanged )
		{
			if( context is null ) throw new ArgumentNullException( nameof(context) );

			Occurrence key = Occurrence.Of( context );

			outcome = null;
			definitionChanged = false;

			lock( _lock )
			{
				EvictExpired();

				if( !_entries.TryGetValue( key, out Entry entry ) )
					return false;

				if( !string.Equals( entry.JobType, context.JobType, StringComparison.Ordinal ) ||
					!string.Equals( entry.Payload, context.Payload, StringComparison.Ordinal ) )
				{
					Remove( key );

					definitionChanged = true;

					return false;
				}

				outcome = entry.Outcome;

				return true;
			}
		}

		/// <summary>Forgets the context's occurrence, if remembered.</summary>
		public void Forget( ScheduledJobContext context )
		{
			if( context is null ) throw new ArgumentNullException( nameof(context) );

			Occurrence key = Occurrence.Of( context );

			lock( _lock )
			{
				EvictExpired();

				if( _entries.ContainsKey( key ) )
					Remove( key );
			}
		}

		// Entries are in insertion order, so the expired ones are a prefix of the list.
		private void EvictExpired()
		{
			while( _order.First is { } oldest &&
					_timeProvider.GetElapsedTime( _entries[oldest.Value].Remembered ) >= Retention )
				Remove( oldest.Value );
		}

		private void Remove( Occurrence key )
		{
			_order.Remove( _entries[key].Node );
			_entries.Remove( key );
		}

		private sealed record Entry( JobOutcome Outcome, string JobType, string Payload, long Remembered,
									LinkedListNode<Occurrence> Node );

		// The occurrence key, normalized so that the same occurrence matches however it came back:
		// UTC (a Local value is converted, an Unspecified one taken as UTC), truncated to whole
		// microseconds — Postgres stores microseconds, so a first claim may carry 100 ns ticks that
		// the adopting claim, read back from the store, no longer has.
		private readonly record struct Occurrence( string Schedule, long ScheduledForTicks )
		{
			public static Occurrence Of( ScheduledJobContext context )
			{
				DateTime scheduledFor = context.ScheduledFor.Kind switch
				{
					DateTimeKind.Local => context.ScheduledFor.ToUniversalTime(),
					_ => context.ScheduledFor
				};

				long ticks = scheduledFor.Ticks - scheduledFor.Ticks % TimeSpan.TicksPerMicrosecond;

				return new Occurrence( context.Schedule, ticks );
			}
		}
	}
}
