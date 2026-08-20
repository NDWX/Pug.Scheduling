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

		protected SchedulerBase( IApplicationData<IDataSession> applicationData,
								ISecurityManager securityManager )
		{
			ApplicationDataProvider = applicationData ??
									throw new ArgumentNullException( nameof(applicationData) );

			SecurityManager = securityManager ?? throw new ArgumentNullException( nameof(securityManager) );
		}

		protected string CurrentUserIdentifier => SecurityManager.CurrentPrincipal.Identity.Identifier;

		protected ActionContext<Reference> CreateActionContext() =>
			new()
			{
				Actor = new Reference { Type = "USER", Identifier = CurrentUserIdentifier },
				Timestamp = DateTime.UtcNow
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
