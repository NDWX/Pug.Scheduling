using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.WebApi.Models
{
	// Body of the 409 response to a run-now request refused because a run of the schedule is in
	// progress: which run, who holds it, and when its lease lapses. Shared by the endpoint that
	// renders it and the API client that reads it back — the client reconstructs the
	// RunInProgressException the facade originally threw.
	[DataContract]
	public record RunInProgressResultInfo
	{
		public RunInProgressResultInfo()
		{
			Schedule = string.Empty;
			Run = string.Empty;
			Holder = string.Empty;
		}

		public RunInProgressResultInfo( string schedule, string run, string holder, DateTime? leaseExpiry )
		{
			Schedule = schedule;
			Run = run;
			Holder = holder;
			LeaseExpiry = leaseExpiry;
		}

		[Required( AllowEmptyStrings = true ), DataMember]
		public string Schedule
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[Required( AllowEmptyStrings = true ), DataMember]
		public string Run
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[Required( AllowEmptyStrings = true ), DataMember]
		public string Holder
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// UTC.
		[DataMember]
		public DateTime? LeaseExpiry
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}
	}
}
