using Pug.Scheduling.Abstractions;

namespace Pug.Scheduling.Tests.Infrastructure;

internal sealed class SequentialIdGenerator : IdentifierGenerator
{
	private readonly string _prefix;
	private int _next;

	public SequentialIdGenerator( string prefix = "id" )
	{
		_prefix = prefix;
	}

	public string GetNext() => $"{_prefix}-{Guid.NewGuid():N}-{Interlocked.Increment( ref _next ):D6}";
}
