using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BankFlow.Api.Services;

public static class OtpInput
{
    public static bool IsValid(ReadOnlySpan<char> code)
    {
        if (code.Length != 6) return false;
        foreach (var character in code)
            if (!char.IsAsciiDigit(character)) return false;
        return true;
    }
}

public sealed class ReplayProtectedAuthenticatorProvider(TimeProvider clock, AuthDbContext db) : AuthenticatorTokenProvider<IdentityUser>
{
    public const string Name = "BankFlowAuthenticator";
    private const string ReplayStore = "BankFlow.Mfa.Replay";

    public override async Task<bool> ValidateAsync(string purpose, string token, UserManager<IdentityUser> manager, IdentityUser user)
    {
        if (!OtpInput.IsValid(token.AsSpan()) || !await base.ValidateAsync(purpose, token, manager, user)) return false;
        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (key is null) return false;
        var fingerprint = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(token)));
        var slot = "Otp" + fingerprint;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var lastUse = await manager.GetAuthenticationTokenAsync(user, ReplayStore, slot);
        if (long.TryParse(lastUse, NumberStyles.None, CultureInfo.InvariantCulture, out var expires) && expires > now)
            return false;
        IdentityResult stored;
        try
        {
            stored = await manager.SetAuthenticationTokenAsync(user, ReplayStore, slot,
                (now + 180).ToString(CultureInfo.InvariantCulture));
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            await db.Entry(user).ReloadAsync();
            return false;
        }
        if (!stored.Succeeded)
        {
            db.ChangeTracker.Clear();
            await db.Entry(user).ReloadAsync();
            return false;
        }
        var previous = await db.UserTokens.Where(entry => entry.UserId == user.Id && entry.LoginProvider == ReplayStore && entry.Name != slot).ToListAsync();
        var expired = previous.Where(entry => long.TryParse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var expiry) && expiry <= now).ToArray();
        if (expired.Length != 0)
        {
            db.UserTokens.RemoveRange(expired);
            await db.SaveChangesAsync();
        }
        return true;
    }
}