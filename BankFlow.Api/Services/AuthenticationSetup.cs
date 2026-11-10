using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

namespace BankFlow.Api.Services;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<IdentityUser>(options);

public static class AuthenticationSetup
{
    public static void AddBankFlowAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite(
            builder.Configuration.GetConnectionString("AuthDb") ?? "Data Source=bankflow-auth.db"));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<MfaMetrics>();
        builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequiredUniqueChars = 4;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.User.RequireUniqueEmail = true;
            options.Tokens.AuthenticatorTokenProvider = ReplayProtectedAuthenticatorProvider.Name;
        }).AddEntityFrameworkStores<AuthDbContext>().AddDefaultTokenProviders()
            .AddTokenProvider<ReplayProtectedAuthenticatorProvider>(ReplayProtectedAuthenticatorProvider.Name);
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "BankFlow.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        builder.Services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.Zero);
        builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
            IdentityConstants.TwoFactorUserIdScheme, options =>
            {
                options.Cookie.Name = "BankFlow.MfaPending";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                options.SlidingExpiration = false;
                options.Events.OnValidatePrincipal = async context =>
                {
                    var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<IdentityUser>>();
                    if (await signIn.ValidateTwoFactorSecurityStampAsync(context.Principal) is null)
                        context.RejectPrincipal();
                };
            });
        builder.Services.AddOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme)
            .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        builder.Services.AddRateLimiter(options =>
        {
            var loginLimit = builder.Environment.IsDevelopment()
                ? Math.Clamp(builder.Configuration.GetValue("RateLimits:LoginPermitLimit", 10), 1, 100) : 10;
            var mfaLimit = builder.Environment.IsDevelopment()
                ? Math.Clamp(builder.Configuration.GetValue("RateLimits:MfaPermitLimit", 30), 1, 100) : 30;
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = loginLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.AddPolicy("mfa", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = mfaLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    public static async Task InitializeAuthenticationAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreatedAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { BankFlowRoles.Operator, BankFlowRoles.Client, BankFlowRoles.Executive })
        {
            if (await roles.RoleExistsAsync(role)) continue;
            var created = await roles.CreateAsync(new IdentityRole(role));
            if (!created.Succeeded) throw new InvalidOperationException("Unable to initialize application roles.");
        }
        await InitializeExecutiveAsync(app, scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>());
        var email = app.Configuration["Bootstrap:Email"];
        var password = app.Configuration["Bootstrap:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new IdentityUser { UserName = email, Email = email };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
                throw new InvalidOperationException("Bootstrap user rejected: " + string.Join(", ", result.Errors.Select(error => error.Code)));
        }
        if (await users.IsInRoleAsync(user, BankFlowRoles.Operator)) return;
        if ((await users.GetRolesAsync(user)).Count != 0)
            throw new InvalidOperationException("Bootstrap user already has a non-operator role.");
        var assigned = await users.AddToRoleAsync(user, BankFlowRoles.Operator);
        if (!assigned.Succeeded) throw new InvalidOperationException("Unable to assign bootstrap operator role.");
        await users.UpdateSecurityStampAsync(user);
    }

    private static async Task InitializeExecutiveAsync(WebApplication app, UserManager<IdentityUser> users)
    {
        var email = app.Configuration["Bootstrap:ExecutiveEmail"];
        var password = app.Configuration["Bootstrap:ExecutivePassword"];
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(password)) return;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Bootstrap:ExecutiveEmail and Bootstrap:ExecutivePassword must be configured together.");

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new IdentityUser { UserName = email, Email = email };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
                throw new InvalidOperationException("Executive bootstrap user rejected: " + string.Join(", ", result.Errors.Select(error => error.Code)));
        }
        if (await users.IsInRoleAsync(user, BankFlowRoles.Executive)) return;
        if ((await users.GetRolesAsync(user)).Count != 0)
            throw new InvalidOperationException("Executive bootstrap user already has a different role.");
        var assigned = await users.AddToRoleAsync(user, BankFlowRoles.Executive);
        if (!assigned.Succeeded) throw new InvalidOperationException("Unable to assign the executive role.");
        await users.UpdateSecurityStampAsync(user);
    }

    public static void MapBankFlowAuthentication(this WebApplication app)
    {
        app.MapGet("/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
            {
                HttpOnly = false,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps || !app.Environment.IsDevelopment(),
                Path = "/"
            });
            return TypedResults.NoContent();
        }).AllowAnonymous();

        app.MapPost("/auth/login", async Task<IResult> (LoginRequest request, SignInManager<IdentityUser> signIn, TimeProvider clock, MfaMetrics metrics) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 254 ||
                string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
                return TypedResults.Unauthorized();
            await signIn.SignOutAsync();
            var candidate = await signIn.UserManager.FindByNameAsync(request.Username.Trim());
            if (candidate is not null && candidate.TwoFactorEnabled &&
                !(await signIn.UserManager.GetValidTwoFactorProvidersAsync(candidate)).Contains(ReplayProtectedAuthenticatorProvider.Name))
                return TypedResults.Unauthorized();
            var result = await signIn.PasswordSignInAsync(request.Username.Trim(), request.Password, false, true);
            if (result.RequiresTwoFactor)
            {
                var user = (await signIn.GetTwoFactorAuthenticationUserAsync())!;
                var identity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
                identity.AddClaim(new Claim(ClaimTypes.Name, user.Id));
                identity.AddClaim(new Claim(signIn.Options.ClaimsIdentity.SecurityStampClaimType,
                    await signIn.UserManager.GetSecurityStampAsync(user)));
                await signIn.Context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(identity),
                    new AuthenticationProperties { ExpiresUtc = clock.GetUtcNow().AddMinutes(5), IsPersistent = false });
                metrics.Record(MfaEvent.Challenge);
                return TypedResults.Accepted(uri: (string?)null, value: new { requiresMfa = true });
            }
            return result.Succeeded ? TypedResults.NoContent() : TypedResults.Unauthorized();
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapGet("/auth/me", async Task<IResult> (HttpContext context, UserManager<IdentityUser> users) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var user = await users.GetUserAsync(context.User);
            return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(new
            {
                user.Id, user.Email, Roles = await users.GetRolesAsync(user)
            });
        }).RequireAuthorization();

        app.MapPost("/auth/logout", async (HttpContext context, UserManager<IdentityUser> users,
            SignInManager<IdentityUser> signIn) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is not null) await users.UpdateSecurityStampAsync(user);
            await signIn.SignOutAsync();
            return TypedResults.NoContent();
        }).RequireAuthorization();
    }
}

public static class BankFlowRoles
{
    public const string Operator = "Operator";
    public const string Client = "Client";
    public const string Executive = "Executive";
    public const string BankingPolicy = "BankingUser";
    public const string OperatorPolicy = "BankingOperator";
    public const string BackofficePolicy = "BankingBackoffice";
}