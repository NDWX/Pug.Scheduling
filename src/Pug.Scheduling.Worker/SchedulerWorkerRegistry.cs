using Pug.Scheduling.Abstractions;

namespace Pug.Scheduling.Worker
{
	/// <summary>
	/// The <see cref="SchedulerWorker"/> instances hosted in this process, by worker number.
	/// </summary>
	public sealed class SchedulerWorkerRegistry : ISchedulerWorkerRegistry
	{
		private readonly IReadOnlyList<SchedulerWorker> _workers;
		private readonly Dictionary<int, SchedulerWorker> _byNumber;

		/// <exception cref="ArgumentException">Two workers share a number.</exception>
		public SchedulerWorkerRegistry( IEnumerable<SchedulerWorker> workers )
		{
			if( workers is null ) throw new ArgumentNullException( nameof(workers) );

			_workers = workers.OrderBy( worker => worker.Number ).ToArray();
			_byNumber = new Dictionary<int, SchedulerWorker>( _workers.Count );

			foreach( SchedulerWorker worker in _workers )
				if( !_byNumber.TryAdd( worker.Number, worker ) )
					throw new ArgumentException( $"More than one worker is numbered {worker.Number}.", nameof(workers) );
		}

		/// <summary>Every hosted worker, in worker-number order.</summary>
		public IReadOnlyList<SchedulerWorker> Workers => _workers;

		/// <summary>The worker with the given number, or null.</summary>
		public SchedulerWorker Find( int worker ) => _byNumber.GetValueOrDefault( worker );

		IReadOnlyCollection<ISchedulerWorker> ISchedulerWorkerRegistry.Workers => _workers;

		ISchedulerWorker ISchedulerWorkerRegistry.Find( int worker ) => Find( worker );
	}
}
