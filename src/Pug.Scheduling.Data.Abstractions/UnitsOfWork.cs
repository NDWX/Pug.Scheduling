using System.Transactions;
using Pug.Application.Data;
using Pug.Application.Data.Extensions;

namespace Pug.Scheduling.Data.Abstractions
{
	// How the schedule store runs units of work (the Pug.Sal pattern). Each runs in a local
	// transaction of its own (RequiresNew), never in a caller's ambient one, whose isolation level
	// could conflict with the one the store needs:
	// - Writes run at ReadCommitted. A write unit that acts on what it read locks that row first
	//   (IDataSession.GetScheduleAsync, RowLock.Wait); at a stricter level, Postgres would fail a writer
	//   that waited for the lock (40001, could not serialize access due to concurrent update)
	//   instead of letting it proceed on the committed row (DECISIONS D21, D22).
	// - Reads run at RepeatableRead, so every statement of one read sees the same snapshot; a
	//   read-only transaction never fails at that level.
	// SQLite, whose session enlists itself, is serializable whatever the level: it begins a write
	// as IMMEDIATE (the database write lock, held to the end) and a read as DEFERRED (a snapshot that
	// blocks nobody).
	public static class UnitsOfWork
	{
		private const TransactionScopeOption Scope = TransactionScopeOption.RequiresNew;

		private static readonly TransactionOptions Read = new() { IsolationLevel = IsolationLevel.RepeatableRead };

		private static readonly TransactionOptions Write = new() { IsolationLevel = IsolationLevel.ReadCommitted };

		public static Task<TResult> ReadAsync<TContext, TResult>( this IApplicationData<IDataSession> data,
																Func<IDataSession, TContext, Task<TResult>> read,
																TContext context ) =>
			data.ExecuteAsync( read, context, Scope, Read );

		public static Task<TResult> WriteAsync<TContext, TResult>( this IApplicationData<IDataSession> data,
																	Func<IDataSession, TContext, Task<TResult>> write,
																	TContext context ) =>
			data.ExecuteAsync( write, context, Scope, Write );

		public static Task WriteAsync<TContext>( this IApplicationData<IDataSession> data,
												Func<IDataSession, TContext, Task> write,
												TContext context ) =>
			data.PerformAsync( write, context, Scope, Write );
	}
}
