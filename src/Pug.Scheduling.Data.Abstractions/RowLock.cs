namespace Pug.Scheduling.Data.Abstractions
{
	// How a schedule read locks the row it returns (IDataSession.GetScheduleAsync,
	// GetScheduleByActiveRunAsync). A lock is held until the unit of work ends, so the unit can act
	// on what it read; only a write unit (UnitsOfWork.WriteAsync) may take one.
	//
	// A storage primitive, not policy: which mode a transition uses is decided by its caller. A
	// provider whose write unit already serializes writers (SQLite's BEGIN IMMEDIATE) takes no row
	// lock in either locking mode: there is no row contention to wait on or to skip.
	public enum RowLock
	{
		// A plain read: no lock.
		None,

		// Locks the row, waiting for a concurrent holder's unit to end first, then returns the row
		// as committed (Postgres 'for update'). Null only when no such row exists.
		Wait,

		// Locks the row only if no concurrent unit holds it, without waiting: a row held by another
		// unit reads as null, exactly like a missing row (Postgres 'for update skip locked'). Only a
		// caller to which "held elsewhere" may mean "pass it over" can use it: to any other, null
		// would wrongly read as "the row does not exist".
		SkipLocked
	}
}
