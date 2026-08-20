using Microsoft.Extensions.DependencyInjection;
using Pug.Scheduling.Abstractions;

namespace Pug.Scheduling.DependencyInjection
{
	// Collects job types declared during service registration. Kept as a mutable singleton
	// instance in the service collection so successive Add* calls accumulate into one registry.
	internal sealed class JobTypeRegistrations
	{
		public HashSet<string> JobTypes { get; } = new();
	}

	internal sealed class JobTypeRegistry : IJobTypeRegistry
	{
		private readonly JobTypeRegistrations _registrations;
		private readonly IServiceProvider _serviceProvider;

		public JobTypeRegistry( JobTypeRegistrations registrations, IServiceProvider serviceProvider )
		{
			_registrations = registrations;
			_serviceProvider = serviceProvider;
		}

		public bool IsRegistered( string jobType ) => _registrations.JobTypes.Contains( jobType );

		public IJobPayloadValidator GetPayloadValidator( string jobType ) =>
			_serviceProvider.GetKeyedService<IJobPayloadValidator>( jobType );
	}
}
