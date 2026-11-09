using System.Security.Claims;
using BankFlow.Api.Filters;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Api.Services;

public sealed record MfaPasswordRequest(string Password);
public sealed record MfaCodeRequest(string Code);
public sealed record MfaProofRequest(string Password, string? Code, string? RecoveryCode);
public sealed record MfaLoginRequest(string? Code, string? RecoveryCode);
public sealed record MfaStatusResponse(bool Enabled, int RecoveryCodesRemaining);
public sealed record MfaSetupResponse(string SharedKey, string AuthenticatorUri);
public sealed record MfaRecoveryResponse(IEnumerable<string> RecoveryCodes);

public sealed class MfaPasswordValidator : AbstractValidator<MfaPasswordRequest>
{
    public MfaPasswordValidator() => RuleFor(request => request.Password).NotEmpty().MaximumLength(256);
}
public sealed class MfaCodeValidator : AbstractValidator<MfaCodeRequest>
{
    public MfaCodeValidator() => RuleFor(request => request.Code).NotEmpty().Must(code => OtpInput.IsValid(code.AsSpan()));
}
public sealed class MfaLoginValidator : AbstractValidator<MfaLoginRequest>
{
    public MfaLoginValidator()
    {
        RuleFor(request => request).Must(request => MfaEndpoints.IsValidProof(request.Code, request.RecoveryCode));
    }
}
public sealed class MfaProofValidator : AbstractValidator<MfaProofRequest>
{
    public MfaProofValidator()
    {
        RuleFor(request => request.Password).NotEmpty().MaximumLength(256);
        RuleFor(request => request).Must(request => MfaEndpoints.IsValidProof(request.Code, request.RecoveryCode));
    }
}

public static class MfaEndpoints
{
    public static bool IsValidProof(string? code, string? recoveryCode) =>
        code is not null && recoveryCode is null && OtpInput.IsValid(code.AsSpan()) ||
        code is null && recoveryCode is { Length: > 0 and <= 32 };

    public static void MapMfa(this WebApplication app)
    {
        var mfa = app.MapGroup("/auth/mfa").RequireAuthorization().RequireRateLimiting("mfa");

        mfa.MapGet("/pending", async (SignInManager<IdentityUser> signIn, UserManager<IdentityUser> users) =>
        {
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            return TypedResults.Ok(new { pending = user is not null && user.TwoFactorEnabled && !await users.IsLockedOutAsync(user) });
        }).AllowAnonymous();

        mfa.MapGet("", async (HttpContext context, UserManager<IdentityUser> users) =>
        {
            var user = (await users.GetUserAsync(context.User))!;
            return TypedResults.Ok(new MfaStatusResponse(user.TwoFactorEnabled, await users.CountRecoveryCodesAsync(user)));
        });

        mfa.MapPost("/setup", async Task<IResult> (MfaPasswordRequest request, HttpContext context,
            UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, AuthDbContext db) =>
        {
            var user = (await users.GetUserAsync(context.User))!;
            if (!(await signIn.CheckPasswordSignInAsync(user, request.Password, true)).Succeeded) return TypedResults.Unauthorized();
            if (user.TwoFactorEnabled) return TypedResults.Conflict(new { code = "mfa_already_enabled" });
            await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
            var key = await users.GetAuthenticatorKeyAsync(user);
            if (key is null)
            {
                Ensure(await users.ResetAuthenticatorKeyAsync(user));
                key = await users.GetAuthenticatorKeyAsync(user);
            }
            await transaction.CommitAsync(context.RequestAborted);
            await signIn.RefreshSignInAsync(user);
            var uri = $"otpauth://totp/{Uri.EscapeDataString("BankFlow:" + user.Email)}?secret={Uri.EscapeDataString(key!)}&issuer=BankFlow&digits=6";
            return TypedResults.Ok(new MfaSetupResponse(key!, uri));
        }).AddEndpointFilter<ValidationFilter<MfaPasswordRequest>>();

        mfa.MapPost("/enable", async Task<IResult> (MfaCodeRequest request, HttpContext context,
            UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, AuthDbContext db, MfaMetrics metrics) =>
        {
            var user = (await users.GetUserAsync(context.User))!;
            if (user.TwoFactorEnabled) return TypedResults.Conflict(new { code = "mfa_already_enabled" });
            await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
            if (await users.IsLockedOutAsync(user) || !await users.VerifyTwoFactorTokenAsync(user,
                ReplayProtectedAuthenticatorProvider.Name, request.Code))
            {
                await users.AccessFailedAsync(user);
                await transaction.CommitAsync(context.RequestAborted);
                metrics.Record(MfaEvent.Rejected);
                return TypedResults.Unauthorized();
            }
            Ensure(await users.SetTwoFactorEnabledAsync(user, true));
            var recovery = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            Ensure(await users.UpdateSecurityStampAsync(user));
            await transaction.CommitAsync(context.RequestAborted);
            await signIn.SignInWithClaimsAsync(user, false, [new Claim("amr", "mfa")]);
            metrics.Record(MfaEvent.Enabled);
            return TypedResults.Ok(new MfaRecoveryResponse(recovery!));
        }).AddEndpointFilter<ValidationFilter<MfaCodeRequest>>();

        mfa.MapPost("/verify", async Task<IResult> (MfaLoginRequest request, SignInManager<IdentityUser> signIn,
            UserManager<IdentityUser> users, MfaMetrics metrics) =>
        {
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            if (user is null || !user.TwoFactorEnabled || await users.IsLockedOutAsync(user)) return TypedResults.Unauthorized();
            var result = request.RecoveryCode is not null
                ? await signIn.TwoFactorRecoveryCodeSignInAsync(request.RecoveryCode.Trim())
                : await signIn.TwoFactorSignInAsync(ReplayProtectedAuthenticatorProvider.Name, request.Code!, false, false);
            if (!result.Succeeded && request.RecoveryCode is not null) await users.AccessFailedAsync(user);
            metrics.Record(result.Succeeded ? MfaEvent.Verified : MfaEvent.Rejected);
            return result.Succeeded ? TypedResults.NoContent() : TypedResults.Unauthorized();
        }).AllowAnonymous().AddEndpointFilter<ValidationFilter<MfaLoginRequest>>();

        mfa.MapPost("/cancel", async (SignInManager<IdentityUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return TypedResults.NoContent();
        }).AllowAnonymous();

        mfa.MapPost("/disable", async Task<IResult> (MfaProofRequest request, HttpContext context,
            UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, AuthDbContext db, MfaMetrics metrics) =>
        {
            var user = (await users.GetUserAsync(context.User))!;
            if (!user.TwoFactorEnabled) return TypedResults.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
            if (!await ValidateProof(request, user, users, signIn))
            {
                await transaction.CommitAsync(context.RequestAborted);
                return TypedResults.Unauthorized();
            }
            Ensure(await users.SetTwoFactorEnabledAsync(user, false));
            await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
            Ensure(await users.ResetAuthenticatorKeyAsync(user));
            Ensure(await users.UpdateSecurityStampAsync(user));
            await transaction.CommitAsync(context.RequestAborted);
            await signIn.SignOutAsync();
            metrics.Record(MfaEvent.Disabled);
            return TypedResults.NoContent();
        }).AddEndpointFilter<ValidationFilter<MfaProofRequest>>();

        mfa.MapPost("/recovery-codes", async Task<IResult> (MfaProofRequest request, HttpContext context,
            UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn, AuthDbContext db, MfaMetrics metrics) =>
        {
            var user = (await users.GetUserAsync(context.User))!;
            if (!user.TwoFactorEnabled) return TypedResults.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
            if (!await ValidateProof(request, user, users, signIn))
            {
                await transaction.CommitAsync(context.RequestAborted);
                return TypedResults.Unauthorized();
            }
            var recovery = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            Ensure(await users.UpdateSecurityStampAsync(user));
            await transaction.CommitAsync(context.RequestAborted);
            await signIn.SignInWithClaimsAsync(user, false, [new Claim("amr", "mfa")]);
            metrics.Record(MfaEvent.RecoveryRegenerated);
            return TypedResults.Ok(new MfaRecoveryResponse(recovery!));
        }).AddEndpointFilter<ValidationFilter<MfaProofRequest>>();
    }

    private static async Task<bool> ValidateProof(MfaProofRequest request, IdentityUser user,
        UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn)
    {
        if (!(await signIn.CheckPasswordSignInAsync(user, request.Password, true)).Succeeded) return false;
        var valid = request.RecoveryCode is not null
            ? (await users.RedeemTwoFactorRecoveryCodeAsync(user, request.RecoveryCode.Trim())).Succeeded
            : await users.VerifyTwoFactorTokenAsync(user, ReplayProtectedAuthenticatorProvider.Name, request.Code!);
        if (!valid) await users.AccessFailedAsync(user);
        return valid;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("MFA state changed concurrently. Try again.");
    }
}