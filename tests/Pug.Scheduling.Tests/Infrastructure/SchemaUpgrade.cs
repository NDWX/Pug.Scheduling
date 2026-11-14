using DbUp.Builder;
using DbUp.Engine;
using Pug.Application.Data;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>Applies a store's <see cref="SchemaUpgradeScript"/>s with DbUp, whichever the provider.</summary>
internal static class SchemaUpgrade
{
	/// <summary>
	/// Applies <paramref name="scripts"/> through <paramref name="builder"/> (already pointed at the
	/// provider's database), in the order given (schema version, then sequence): DbUp would
	/// otherwise sort by name.
	/// </summary>
	public static void Apply( UpgradeEngineBuilder builder, IEnumerable<SchemaUpgradeScript> scripts )
	{
		int order = 0;

		foreach( SchemaUpgradeScript script in scripts )
			builder = builder.WithScript(
					script.UpgradeScript.Name, script.UpgradeScript.Script,
					new SqlScriptOptions { RunGroupOrder = order++ } );

		DatabaseUpgradeResult result = builder.LogToNowhere().Build().PerformUpgrade();

		if( !result.Successful )
			throw new InvalidOperationException( "DbUp schema migration failed.", result.Error );
	}
}
