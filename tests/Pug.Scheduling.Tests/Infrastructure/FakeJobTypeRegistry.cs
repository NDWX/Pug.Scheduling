using Pug.Scheduling.Abstractions;

namespace Pug.Scheduling.Tests.Infrastructure;

internal sealed class FakeJobTypeRegistry : IJobTypeRegistry
{
	private readonly Dictionary<string, IJobPayloadValidator?> _jobTypes = new();

	public FakeJobTypeRegistry( params string[] jobTypes )
	{
		foreach( string jobType in jobTypes )
			_jobTypes[jobType] = null;
	}

	public void Register( string jobType, IJobPayloadValidator? validator = null ) =>
		_jobTypes[jobType] = validator;

	public bool IsRegistered( string jobType ) => _jobTypes.ContainsKey( jobType );

	public IJobPayloadValidator GetPayloadValidator( string jobType ) =>
		_jobTypes.GetValueOrDefault( jobType )!;
}
