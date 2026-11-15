using System;

namespace Pug.Scheduling.WebApi.Models
{
	/// <summary>
	/// The default base path of each HTTP API, defined once so the endpoints a host maps and the
	/// clients that call them cannot drift apart. A host may map an API elsewhere; its clients
	/// then take the same base path.
	/// </summary>
	public static class ApiBasePaths
	{
		public const string Schedules = "schedules";

		public const string ExecutionCoordinator = "executionCoordinator";

		public const string Workers = "workers";

		/// <summary>
		/// The base path as it is mapped and requested: without leading or trailing <c>/</c>.
		/// Server options and clients both go through this, so they agree on what a given base
		/// path means. Throws <see cref="ArgumentException"/> when nothing is left after trimming.
		/// </summary>
		public static string Normalize( string basePath, string paramName )
		{
			string trimmed = ( basePath ?? string.Empty ).Trim().Trim( '/' );

			if( trimmed.Length == 0 )
				throw new ArgumentException( "A base path is required.", paramName );

			return trimmed;
		}
	}
}
