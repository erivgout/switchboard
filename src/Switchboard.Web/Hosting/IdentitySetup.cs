using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Web.Hosting;

/// <summary>Adds the display name to the auth cookie so layouts don't need a lookup for it.</summary>
public sealed class SwitchboardClaimsFactory(UserManager<User> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<User>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(SwitchboardClaims.DisplayName, user.DisplayName));
        return identity;
    }
}

public static class IdentitySetup
{
    public static IServiceCollection AddSwitchboardIdentity(this IServiceCollection services, IWebHostEnvironment env)
    {
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = IdentityConstants.ApplicationScheme;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies(cookies => cookies.ApplicationCookie!.Configure(c =>
            {
                c.LoginPath = "/account/login";
                c.LogoutPath = "/account/logout";
                c.AccessDeniedPath = "/status/403";
                c.Cookie.Name = "sb.auth";
                c.Cookie.HttpOnly = true;
                c.Cookie.SameSite = SameSiteMode.Lax;
                c.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                c.ExpireTimeSpan = TimeSpan.FromHours(12);
                c.SlidingExpiration = true;
                c.Events.OnRedirectToLogin = ctx => RedirectOrStatus(ctx.HttpContext, ctx.RedirectUri, StatusCodes.Status401Unauthorized);
                c.Events.OnRedirectToAccessDenied = ctx => RedirectOrStatus(ctx.HttpContext, ctx.RedirectUri, StatusCodes.Status403Forbidden);
            }));

        services.AddIdentityCore<User>(o =>
            {
                o.User.RequireUniqueEmail = true;
                // Length over composition rules (NIST 800-63B).
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
                o.SignIn.RequireConfirmedAccount = false;
            })
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddEntityFrameworkStores<SwitchboardDbContext>()
            .AddClaimsPrincipalFactory<SwitchboardClaimsFactory>();

        services.AddScoped<IPasswordHasher<User>, Argon2PasswordHasher>();
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));
        return services;
    }

    private static Task RedirectOrStatus(HttpContext http, string redirectUri, int status)
    {
        // APIs and htmx calls get a status code instead of a login page.
        if (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/mcp")
            || http.Request.Headers.ContainsKey("HX-Request") || http.Request.Path.StartsWithSegments("/hubs"))
        {
            http.Response.StatusCode = status;
            return Task.CompletedTask;
        }
        http.Response.Redirect(redirectUri);
        return Task.CompletedTask;
    }
}

/// <summary>Signs out cookie sessions of users who were deactivated (INV-ID-4).</summary>
public sealed class ActiveUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SwitchboardDbContext db)
    {
        if (context.User.Identity is { IsAuthenticated: true, AuthenticationType: "Identity.Application" }
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            var active = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => (bool?)u.IsActive).FirstOrDefaultAsync();
            if (active != true)
            {
                await context.RequestServices.GetRequiredService<SignInManager<User>>().SignOutAsync();
                context.Response.Redirect("/account/login");
                return;
            }
        }
        await next(context);
    }
}
