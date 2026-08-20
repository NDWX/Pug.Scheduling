using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.WebApi;

[DataContract]
public record BadRequestResultInfo
{
	public BadRequestResultInfo()
	{
		Message = string.Empty;
		Type = string.Empty;
		RequestArgument = string.Empty;
	}

	public BadRequestResultInfo( string type, string requestArgument, string message = "" )
	{
		Type = type;
		RequestArgument = requestArgument;
		Message = message;
	}

	[Required( AllowEmptyStrings = true ), DataMember]
	public string Message { get; init; }

	[Required( AllowEmptyStrings = true ), DataMember]
	public string Type { get; init; }

	[Required( AllowEmptyStrings = true ), DataMember]
	public string RequestArgument { get; init; }
}
