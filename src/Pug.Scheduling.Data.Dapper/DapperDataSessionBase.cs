using System.Data;
using Dapper;
using Pug.Application.Data;
using Pug.Effable;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Data.Dapper
{
	// The schedule store's SQL and row mapping, shared by every Dapper provider. A provider
	// subclasses it with its dialect: the enabled-and-not-paused predicate, the clause that
	// locks a row read, and how stored timestamps are read back. Everything else - statements,
	// parameters, the flat row shape and its composition into ScheduleInfo - is written once
	// here. The schema scripts stay with each provider's DapperData.
	//
	// Storage primitives only: no statement here decides anything (see IDataSession).
	public abstract class DapperDataSessionBase : ApplicationDataSession, IDataSession
	{
		static DapperDataSessionBase()
		{
			SqlMapper.AddTypeHandler( new JsonTypeHandler<ActionContext<Reference>>() );
			SqlMapper.AddTypeHandler( new JsonTypeHandler<RunSummary>() );
			SqlMapper.AddTypeHandler( new JsonTypeHandler<JobOutcome>() );
		}

		protected DapperDataSessionBase( IDbConnection databaseSession ) : base( databaseSession )
		{
		}

		// == Dialect ==

		// Predicate on the schedule table selecting enabled, unpaused schedules. Written exactly as
		// in the provider's partial due indexes, so the planner can match them.
		protected abstract string EnabledNotPausedPredicate { get; }

		// Appended to a row read that must hold the row's write lock until the unit ends, or empty
		// when the provider's write unit already serializes writers (SQLite's BEGIN IMMEDIATE).
		protected abstract string LockingReadClause { get; }

		// Maps a timestamp as the provider returns it to the UTC DateTime the domain uses
		// (DECISIONS D12, D18). The identity by default, for providers whose column type carries
		// the offset.
		protected virtual DateTime ReadTimestamp( DateTime stored ) => stored;

		private DateTime? ReadTimestamp( DateTime? stored ) => stored is null ? null : ReadTimestamp( stored.Value );

		private ScheduleRun? ReadRun( ScheduleRun? run ) =>
			run is null
				? null
				: run with
				{
					ScheduledFor = ReadTimestamp( run.ScheduledFor ),
					Started = ReadTimestamp( run.Started ),
					Finished = ReadTimestamp( run.Finished )
				};

		private string LockClause( bool lockRow ) =>
			lockRow && !string.IsNullOrEmpty( LockingReadClause ) ? " " + LockingReadClause : string.Empty;

		// == Row shape ==

		// Flat row shape of the schedule table; composed into the sectioned ScheduleInfo model.
		private sealed class ScheduleRow
		{
			public string identifier { get; set; } = null!;
			public string domain { get; set; } = null!;
			public string ownerType { get; set; } = null!;
			public string owner { get; set; } = null!;
			public string name { get; set; } = null!;
			public string jobType { get; set; } = null!;
			public string? payload { get; set; }
			public string? cronExpression { get; set; }
			public long? intervalSeconds { get; set; }
			public string timeZone { get; set; } = null!;
			public DateTime? notBefore { get; set; }
			public DateTime? notAfter { get; set; }
			public string misfireHandling { get; set; } = null!;
			public long misfireThresholdSeconds { get; set; }
			public int maxAttempts { get; set; }
			public long retryBackoffSeconds { get; set; }
			public long timeoutSeconds { get; set; }
			public bool enabled { get; set; }
			public bool paused { get; set; }
			public DateTime? nextRun { get; set; }
			public bool runRequested { get; set; }
			public int attempt { get; set; }
			public RunSummary? lastRun { get; set; }
			public ActionContext<Reference> registration { get; set; } = null!;
			public ActionContext<Reference>? lastUpdate { get; set; }
		}

		private const string ScheduleColumns =
			@"identifier, domain, ownerType, owner, name, jobType, payload, cronExpression, intervalSeconds,
				timeZone, notBefore, notAfter, misfireHandling, misfireThresholdSeconds, maxAttempts,
				retryBackoffSeconds, timeoutSeconds, enabled, paused, nextRun, runRequested, attempt, lastRun,
				registration, lastUpdate";

		private const string RunColumns =
			@"identifier, schedule, domain, scheduledFor, started, finished, attempt,
				runTrigger as Trigger, result, outcome, claimedBy";

		private ScheduleInfo Compose( ScheduleRow row ) =>
			new()
			{
				Identifier = row.identifier,
				Owner = new Reference { Type = row.ownerType, Identifier = row.owner },
				Definition = new ScheduleDefinition
				{
					Domain = row.domain,
					Name = row.name,
					JobType = row.jobType,
					Payload = row.payload,
					Recurrence = new Recurrence
					{
						CronExpression = row.cronExpression,
						Interval = row.intervalSeconds is null
										? null
										: TimeSpan.FromSeconds( row.intervalSeconds.Value ),
						TimeZone = row.timeZone,
						NotBefore = ReadTimestamp( row.notBefore ),
						NotAfter = ReadTimestamp( row.notAfter )
					},
					MisfirePolicy = new MisfirePolicy
					{
						Handling = row.misfireHandling,
						Threshold = TimeSpan.FromSeconds( row.misfireThresholdSeconds )
					},
					RetryPolicy = new RetryPolicy
					{
						MaximumAttempts = row.maxAttempts,
						Backoff = TimeSpan.FromSeconds( row.retryBackoffSeconds )
					},
					Timeout = TimeSpan.FromSeconds( row.timeoutSeconds )
				},
				State = new ScheduleState
				{
					Enabled = row.enabled,
					Paused = row.paused,
					NextRun = ReadTimestamp( row.nextRun ),
					RunRequested = row.runRequested,
					Attempt = row.attempt,
					LastRun = row.lastRun
				},
				Registration = row.registration,
				LastUpdate = row.lastUpdate
			};

		// The definition columns' parameter values, shared by insert and definition update.
		private static long? IntervalSeconds( Recurrence recurrence ) =>
			recurrence.Interval is null ? null : (long)recurrence.Interval.Value.TotalSeconds;

		// == Management operations ==

		public Task InsertAsync( ScheduleInfo schedule )
		{
			return Connection.ExecuteAsync(
					$@"insert into schedule({ScheduleColumns})
						values(@identifier, @domain, @ownerType, @owner, @name, @jobType, @payload, @cronExpression,
							@intervalSeconds, @timeZone, @notBefore, @notAfter, @misfireHandling,
							@misfireThresholdSeconds, @maxAttempts, @retryBackoffSeconds, @timeoutSeconds,
							@enabled, @paused, @nextRun, @runRequested, @attempt, @lastRun, @registration, @lastUpdate);",
					new
					{
						identifier = schedule.Identifier,
						domain = schedule.Definition.Domain,
						ownerType = schedule.Owner.Type,
						owner = schedule.Owner.Identifier,
						name = schedule.Definition.Name,
						jobType = schedule.Definition.JobType,
						payload = schedule.Definition.Payload,
						cronExpression = schedule.Definition.Recurrence.CronExpression,
						intervalSeconds = IntervalSeconds( schedule.Definition.Recurrence ),
						timeZone = schedule.Definition.Recurrence.TimeZone,
						notBefore = schedule.Definition.Recurrence.NotBefore,
						notAfter = schedule.Definition.Recurrence.NotAfter,
						misfireHandling = schedule.Definition.MisfirePolicy.Handling,
						misfireThresholdSeconds = (long)schedule.Definition.MisfirePolicy.Threshold.TotalSeconds,
						maxAttempts = schedule.Definition.RetryPolicy.MaximumAttempts,
						retryBackoffSeconds = (long)schedule.Definition.RetryPolicy.Backoff.TotalSeconds,
						timeoutSeconds = (long)schedule.Definition.Timeout.TotalSeconds,
						enabled = schedule.State.Enabled,
						paused = schedule.State.Paused,
						nextRun = schedule.State.NextRun,
						runRequested = schedule.State.RunRequested,
						attempt = schedule.State.Attempt,
						lastRun = schedule.State.LastRun,
						registration = schedule.Registration,
						lastUpdate = schedule.LastUpdate
					}
				);
		}

		public async Task<ScheduleInfo?> GetScheduleAsync( string identifier, bool lockRow = false )
		{
			ScheduleRow? row = await Connection.QueryFirstOrDefaultAsync<ScheduleRow?>(
									$"select {ScheduleColumns} from schedule where identifier = @identifier{LockClause( lockRow )};",
									new { identifier }
								).ConfigureAwait( false );

			return row is null ? null : Compose( row );
		}

		public async Task<IEnumerable<ScheduleInfo>> GetSchedulesAsync( string domain, Reference? owner = null,
																		string? jobType = null )
		{
			string filter = "domain = @domain";

			if( owner is not null )
				filter += " and owner = @owner";

			if( jobType is not null )
				filter += " and jobType = @jobType";

			IEnumerable<ScheduleRow> rows = await Connection.QueryAsync<ScheduleRow>(
													$"select {ScheduleColumns} from schedule where {filter} order by name;",
													new { domain, owner = owner?.Identifier, jobType }
												).ConfigureAwait( false );

			return rows.Select( Compose ).ToList();
		}

		public Task UpdateDefinitionAsync( string schedule, ScheduleDefinition definition, DateTime? nextRun,
											ActionContext<Reference> lastUpdate )
		{
			return Connection.ExecuteAsync(
					@"update schedule set
							name = @name, jobType = @jobType, payload = @payload,
							cronExpression = @cronExpression, intervalSeconds = @intervalSeconds,
							timeZone = @timeZone, notBefore = @notBefore, notAfter = @notAfter,
							misfireHandling = @misfireHandling, misfireThresholdSeconds = @misfireThresholdSeconds,
							maxAttempts = @maxAttempts, retryBackoffSeconds = @retryBackoffSeconds,
							timeoutSeconds = @timeoutSeconds, nextRun = @nextRun, lastUpdate = @lastUpdate
						where identifier = @identifier;",
					new
					{
						identifier = schedule,
						name = definition.Name,
						jobType = definition.JobType,
						payload = definition.Payload,
						cronExpression = definition.Recurrence.CronExpression,
						intervalSeconds = IntervalSeconds( definition.Recurrence ),
						timeZone = definition.Recurrence.TimeZone,
						notBefore = definition.Recurrence.NotBefore,
						notAfter = definition.Recurrence.NotAfter,
						misfireHandling = definition.MisfirePolicy.Handling,
						misfireThresholdSeconds = (long)definition.MisfirePolicy.Threshold.TotalSeconds,
						maxAttempts = definition.RetryPolicy.MaximumAttempts,
						retryBackoffSeconds = (long)definition.RetryPolicy.Backoff.TotalSeconds,
						timeoutSeconds = (long)definition.Timeout.TotalSeconds,
						nextRun,
						lastUpdate
					}
				);
		}

		public async Task DeleteAsync( string schedule )
		{
			// Run history goes with the schedule; explicit deletes rather than relying on
			// provider-specific foreign-key cascade enforcement.
			await Connection.ExecuteAsync(
						"delete from scheduleRun where schedule = @schedule;", new { schedule } )
					.ConfigureAwait( false );

			await Connection.ExecuteAsync(
						"delete from schedule where identifier = @schedule;", new { schedule } )
					.ConfigureAwait( false );
		}

		public Task SetPausedAsync( string schedule, bool paused )
		{
			return Connection.ExecuteAsync(
					"update schedule set paused = @paused where identifier = @schedule;",
					new { schedule, paused }
				);
		}

		public Task UpdateNextRunAsync( string schedule, DateTime? nextRun )
		{
			return Connection.ExecuteAsync(
					"update schedule set nextRun = @nextRun, runRequested = @runRequested where identifier = @schedule;",
					new { schedule, nextRun, runRequested = false }
				);
		}

		public Task RequestRunAsync( string schedule, DateTime requestedTime )
		{
			return Connection.ExecuteAsync(
					"update schedule set nextRun = @requestedTime, runRequested = @runRequested where identifier = @schedule;",
					new { schedule, requestedTime, runRequested = true }
				);
		}

		public Task ClearRunRequestAsync( string schedule )
		{
			return Connection.ExecuteAsync(
					"update schedule set runRequested = @runRequested where identifier = @schedule;",
					new { schedule, runRequested = false }
				);
		}

		// == Execution operations ==

		public async Task<IEnumerable<ScheduleInfo>> GetDueSchedulesAsync( DateTime dueBy, string? jobType, int maximumSchedules )
		{
			// Two statements rather than '@jobType is null or jobType = @jobType': a null
			// parameter carries no type, which Npgsql rejects, and the unfiltered read keeps the
			// plain due-index plan.
			string jobTypeFilter = jobType is null ? string.Empty : "and jobType = @jobType";

			IEnumerable<ScheduleRow> rows = await Connection.QueryAsync<ScheduleRow>(
													$@"select {ScheduleColumns} from schedule
														where {EnabledNotPausedPredicate}
															{jobTypeFilter}
															and nextRun is not null and nextRun <= @dueBy
														order by nextRun
														limit @maximumSchedules;",
													new { dueBy, jobType, maximumSchedules }
												).ConfigureAwait( false );

			return rows.Select( Compose ).ToList();
		}

		public Task InsertRunAsync( ScheduleRun run )
		{
			return Connection.ExecuteAsync(
					@"insert into scheduleRun(identifier, schedule, domain, scheduledFor, started, finished,
							attempt, runTrigger, result, outcome, claimedBy)
						values(@identifier, @schedule, @domain, @scheduledFor, @started, @finished,
							@attempt, @trigger, @result, @outcome, @claimedBy);",
					new
					{
						identifier = run.Identifier,
						schedule = run.Schedule,
						domain = run.Domain,
						scheduledFor = run.ScheduledFor,
						started = run.Started,
						finished = run.Finished,
						attempt = run.Attempt,
						trigger = run.Trigger,
						result = run.Result,
						outcome = run.Outcome,
						claimedBy = run.ClaimedBy
					}
				);
		}

		public async Task<ScheduleRun?> GetRunAsync( string identifier )
		{
			return ReadRun(
					await Connection.QueryFirstOrDefaultAsync<ScheduleRun?>(
									$"select {RunColumns} from scheduleRun where identifier = @identifier;",
									new { identifier }
								)
								.ConfigureAwait( false )
				);
		}

		public async Task<ScheduleRun?> GetLatestRunAsync( string schedule )
		{
			return ReadRun(
					await Connection.QueryFirstOrDefaultAsync<ScheduleRun?>(
									$@"select {RunColumns} from scheduleRun where schedule = @schedule
										order by started desc limit 1;",
									new { schedule }
								)
								.ConfigureAwait( false )
				);
		}

		public async Task<bool> ClaimRunAsync( string run, string claimedBy )
		{
			int updated = await Connection.ExecuteAsync(
									"update scheduleRun set claimedBy = @claimedBy where identifier = @run and finished is null;",
									new { run, claimedBy }
								).ConfigureAwait( false );

			return updated > 0;
		}

		public async Task<bool> CompleteRunAsync( string run, string claimedBy, DateTime finished, string result,
												JobOutcome? outcome )
		{
			int updated = await Connection.ExecuteAsync(
									@"update scheduleRun set finished = @finished, result = @result, outcome = @outcome
										where identifier = @run and finished is null
											and (claimedBy = @claimedBy or claimedBy is null);",
									new { run, claimedBy, finished, result, outcome }
								).ConfigureAwait( false );

			return updated > 0;
		}

		public Task RecordOccurrenceCompletionAsync( string schedule, int attempt, DateTime? nextRun,
													RunSummary lastRun )
		{
			return Connection.ExecuteAsync(
					@"update schedule set attempt = @attempt, nextRun = @nextRun, lastRun = @lastRun,
							runRequested = @runRequested
						where identifier = @schedule;",
					new { schedule, attempt, nextRun, lastRun, runRequested = false }
				);
		}

		public async Task<IEnumerable<ScheduleRun>> GetRunsAsync( string schedule, DateTime? from = null,
																DateTime? to = null )
		{
			string filter = "schedule = @schedule";

			if( from is not null )
				filter += " and started >= @from";

			if( to is not null )
				filter += " and started <= @to";

			IEnumerable<ScheduleRun> runs = await Connection.QueryAsync<ScheduleRun>(
													$"select {RunColumns} from scheduleRun where {filter} order by started desc;",
													new { schedule, from, to }
												).ConfigureAwait( false );

			return runs.Select( run => ReadRun( run )! ).ToList();
		}
	}
}
