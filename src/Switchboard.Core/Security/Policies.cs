using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Switchboard.Core.Security;

/// <summary>
/// Authorization policy names. Every endpoint requires a signed-in user by default (fallback policy);
/// agent pages add <see cref="Agent"/>, admin pages add <see cref="Privilege"/>.
/// </summary>
/// <example><c>[Authorize(Policy = SwitchboardPolicies.Agent)]</c> or <c>[Authorize(Policy = "priv:kb.manage")]</c></example>
public static class SwitchboardPolicies
{
    public const string Agent = "sb.agent";
    public const string OrgAdmin = "sb.org_admin";
    public const string PrivilegePrefix = "priv:";

    /// <summary>Policy name requiring a privilege in at least one workspace.</summary>
    public static string Privilege(string privilege) => PrivilegePrefix + privilege;
}

public sealed class AgentRequirement : IAuthorizationRequirement;

public sealed class OrgAdminRequirement : IAuthorizationRequirement;

public sealed record PrivilegeRequirement(string Privilege) : IAuthorizationRequirement;

internal sealed class SwitchboardAuthorizationHandler(IPermissionService permissions) : IAuthorizationHandler
{
    public async Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case AgentRequirement:
                    if ((await permissions.CurrentAsync())?.IsAgent == true)
                    {
                        context.Succeed(requirement);
                    }
                    break;
                case OrgAdminRequirement:
                    if ((await permissions.CurrentAsync())?.IsOrgAdmin == true)
                    {
                        context.Succeed(requirement);
                    }
                    break;
                case PrivilegeRequirement p:
                    if (await permissions.HasAsync(p.Privilege))
                    {
                        context.Succeed(requirement);
                    }
                    break;
            }
        }
    }
}

/// <summary>Builds <c>priv:&lt;key&gt;</c> policies on demand.</summary>
internal sealed class SwitchboardPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(SwitchboardPolicies.PrivilegePrefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PrivilegeRequirement(policyName[SwitchboardPolicies.PrivilegePrefix.Length..]))
                .Build();
        }
        return await base.GetPolicyAsync(policyName);
    }
}

public static class AuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddSwitchboardAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, SwitchboardPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, SwitchboardAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(SwitchboardPolicies.Agent, p => p.RequireAuthenticatedUser().AddRequirements(new AgentRequirement()))
            .AddPolicy(SwitchboardPolicies.OrgAdmin, p => p.RequireAuthenticatedUser().AddRequirements(new OrgAdminRequirement()));
        return services;
    }
}

/// <summary>Extra Content-Security-Policy sources modules may need (e.g. a plugin frame host).</summary>
public sealed class ContentSecurityPolicyOptions
{
    public List<string> ImgSrc { get; } = [];
    public List<string> ConnectSrc { get; } = [];
    public List<string> FrameSrc { get; } = [];
    public List<string> FormAction { get; } = [];
}
