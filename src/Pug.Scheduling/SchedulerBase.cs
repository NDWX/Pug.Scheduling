using Pug.Application.Data;
using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	public abstract class SchedulerBase
	{
		protected IApplicationData<IDataSession> ApplicationDataProvider { get; }
		protected ISecurityManager SecurityManager { get; }

		// Read inside each unit of work that needs the time, through its session (ISchedulingClock).
		protected ISchedulingClock Clock { get; }

		protected SchedulerBase( IApplicationData<IDataSession> applicationData,
								ISecurityManager securityManager,
								ISchedulingClock clock )
		{
			ApplicationDataProvider = applicationData ??
									throw new ArgumentNullException( nameof(applicationData) );

			SecurityManager = securityManager ?? throw new ArgumentNullException( nameof(securityManager) );

			Clock = clock ?? throw new ArgumentNullException( nameof(clock) );
		}

		protected string CurrentUserIdentifier => SecurityManager.CurrentPrincipal.Identity.Identifier;

		// The current principal acting at timestamp, the clock's time in the unit that records it.
		protected ActionContext<Reference> CreateActionContext( DateTime timestamp ) =>
			new()
			{
				Actor = new Reference { Type = "USER", Identifier = CurrentUserIdentifier },
				Timestamp = timestamp
			};

		protected async Task CheckAuthorizationAsync( string operation, NounQualifier domainObject,
													string purpose = "",
													IDictionary<string, string> context = null )
		{
			if( context == null )
				context = new Dictionary<string, string>( 0 );

			bool isAuthorized = await SecurityManager.CurrentPrincipal.IsAuthorizedAsync(
														context,
														operation,
														domainObject,
														purpose
													)
													.ConfigureAwait( false );

			if( !isAuthorized )
				throw new NotAuthorized();
		}

		// Whether the given owner principal is the current security principal.
		protected bool IsCurrentUser( Reference owner ) =>
			owner?.Type == "USER" && owner.Identifier == CurrentUserIdentifier;

		// The owner rule: the schedule's Owner principal may always operate on their own
		// schedule; anyone else falls through to the role-based ACL check.
		protected async Task CheckScheduleAuthorizationAsync( string operation, ScheduleInfo schedule )
		{
			if( IsCurrentUser( schedule.Owner ) )
				return;

			await CheckAuthorizationAsync(
						operation,
						ResourceQualifiers.Schedule( schedule.Definition.Domain, schedule.Identifier )
					)
					.ConfigureAwait( false );
		}
	}
}
