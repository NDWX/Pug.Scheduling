using System.Data;
using System.Text.Json;
using Dapper;

namespace Pug.Scheduling.Data.Postgres
{
	// Stores an arbitrary reference-type value as a JSON text column and reads it back. Used for
	// the ActionContext<Reference> / RunSummary / JobOutcome columns that are persisted as JSON.
	internal sealed class JsonTypeHandler<T> : SqlMapper.TypeHandler<T> where T : class
	{
		public override T Parse( object value )
		{
			return value is string text && !string.IsNullOrEmpty( text )
				? JsonSerializer.Deserialize<T>( text )!
				: null!;
		}

		public override void SetValue( IDbDataParameter parameter, T? value )
		{
			parameter.DbType = DbType.String;
			parameter.Value = value is null ? (object)DBNull.Value : JsonSerializer.Serialize( value );
		}
	}
}
