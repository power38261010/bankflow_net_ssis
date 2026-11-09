using System.Net;
using System.Net.Http.Json;
using BankFlow.Api.Services;
using BankFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Identity;
using BankFlow.Domain;
using System.Text.Json;
using BankFlow.Application.Interfaces;
using BankFlow.Application.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BankFlow.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task Login_ShouldRejectMissingCsrfAndAnonymousAccounts()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/accounts")).StatusCode);
        var response = await client.PostAsJsonAsync("/auth/login", new { username = "operator@bankflow.test", password = AuthFactory.Password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_ShouldRestoreSessionAndLogoutShouldRevokeCopiedCookie()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        await SetCsrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, AuthFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/me")).StatusCode);
        var identity = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/auth/me");
        Assert.Equal("Operator", identity.GetProperty("roles")[0].GetString());
        await SetCsrf(client);
        var loginResponse = await Login(client, AuthFactory.Password);
        var sessionCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .Last(value => value.StartsWith("BankFlow.Session=")).Split(';')[0];
        Assert.Contains("httponly", loginResponse.Headers.GetValues("Set-Cookie").First(), StringComparison.OrdinalIgnoreCase);
        await SetCsrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        using var copied = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        copied.DefaultRequestHeaders.Add("Cookie", sessionCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_ShouldLockAccountAfterFiveFailures()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        await SetCsrf(client);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, "WrongPassword123!")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, AuthFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task Login_ShouldRateLimitRepeatedRequests()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        await SetCsrf(client);
        for (var attempt = 0; attempt < 10; attempt++)
            await Login(client, "WrongPassword123!");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(client, AuthFactory.Password)).StatusCode);
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string password) =>
        client.PostAsJsonAsync("/auth/login", new { username = "operator@bankflow.test", password });

    [Fact]
    public async Task Mfa_ShouldEnrollRequireCodeRejectReplayAndAllowRecoveryOnce()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        await AuthenticateAs(client, "operator@bankflow.test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/mfa/setup", new { password = "Incorrect123!" })).StatusCode);
        var (key, recovery) = await EnrollMfa(client);
        Assert.Equal(10, recovery.Length);
        var status = await client.GetFromJsonAsync<JsonElement>("/auth/mfa");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        using var challenge = factory.CreateClient();
        await SetCsrf(challenge);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(challenge, AuthFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await challenge.GetAsync("/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await challenge.PostAsJsonAsync("/auth/mfa/verify", new { code = Code(key) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await challenge.PostAsJsonAsync("/auth/mfa/verify", new { code = Code(key, 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await challenge.GetAsync("/accounts")).StatusCode);
        await SetCsrf(challenge);
        Assert.Equal(HttpStatusCode.NoContent, (await challenge.PostAsJsonAsync("/auth/logout", new { })).StatusCode);
        await SetCsrf(challenge);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(challenge, AuthFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await challenge.PostAsJsonAsync("/auth/mfa/verify", new { recoveryCode = recovery[0] })).StatusCode);
        await SetCsrf(challenge);
        status = await challenge.GetFromJsonAsync<JsonElement>("/auth/mfa");
        Assert.Equal(9, status.GetProperty("recoveryCodesRemaining").GetInt32());
        await challenge.PostAsJsonAsync("/auth/logout", new { });
        await SetCsrf(challenge);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(challenge, AuthFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await challenge.PostAsJsonAsync("/auth/mfa/verify", new { recoveryCode = recovery[0] })).StatusCode);
    }

    [Fact]
    public async Task Mfa_ShouldRevokePendingChallengesAndSessionsWhenDisabled()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        await AuthenticateAs(client, "operator@bankflow.test");
        var (key, recovery) = await EnrollMfa(client);
        using var pending = factory.CreateClient();
        await SetCsrf(pending);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(pending, AuthFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/mfa/disable", new
        {
            password = AuthFactory.Password, recoveryCode = recovery[0]
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending.PostAsJsonAsync("/auth/mfa/verify", new { code = Code(key, 1) })).StatusCode);
        await SetCsrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await Login(client, AuthFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task Mfa_ShouldReplaceRecoveryCodesAndRequirePasswordAndSecondFactor()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        await AuthenticateAs(client, "operator@bankflow.test");
        var (_, original) = await EnrollMfa(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/mfa/recovery-codes", new { password = AuthFactory.Password })).StatusCode);
        var response = await client.PostAsJsonAsync("/auth/mfa/recovery-codes", new { password = AuthFactory.Password, recoveryCode = original[0] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replacement = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(code => code.GetString()).ToArray();
        Assert.Equal(10, replacement.Length);
        using var pending = factory.CreateClient();
        await SetCsrf(pending);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(pending, AuthFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending.PostAsJsonAsync("/auth/mfa/verify", new { recoveryCode = original[1] })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await pending.PostAsJsonAsync("/auth/mfa/verify", new { recoveryCode = replacement[0] })).StatusCode);
    }

    [Fact]
    public async Task Mfa_ShouldExpireChallengeAndLockOutRepeatedFailures()
    {
        var clock = new AdjustableClock();
        await using var factory = new AuthFactory(clock);
        using var client = factory.CreateClient();
        await AuthenticateAs(client, "operator@bankflow.test");
        var (key, _) = await EnrollMfa(client);
        using var pending = factory.CreateClient();
        await SetCsrf(pending);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(pending, AuthFactory.Password)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending.PostAsJsonAsync("/auth/mfa/verify", new { code = Code(key, 1) })).StatusCode);
        using var fresh = factory.CreateClient();
        await SetCsrf(fresh);
        Assert.Equal(HttpStatusCode.Accepted, (await Login(fresh, AuthFactory.Password)).StatusCode);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/auth/mfa/verify", new { recoveryCode = "invalid-recovery" })).StatusCode);
        using var denied = factory.CreateClient();
        await SetCsrf(denied);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(denied, AuthFactory.Password)).StatusCode);
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("123 456", false)]
    [InlineData("abcdef", false)]
    [InlineData("\u0661\u0662\u0663\u0664\u0665\u0666", false)]
    [InlineData("", false)]
    public void Mfa_CodeValidationShouldBeStrict(string input, bool expected) => Assert.Equal(expected, OtpInput.IsValid(input.AsSpan()));

    [Fact]
    public async Task Mfa_ShouldAcceptOnlyOneConcurrentUseOfTheSameCode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bankflow-mfa-test-{Guid.NewGuid():N}.db");
        var gate = new MfaCommitGate();
        try
        {
            await using var factory = new AuthFactory(authConnection: $"Data Source={path};Pooling=False", interceptor: gate);
            using var owner = factory.CreateClient();
            await AuthenticateAs(owner, "operator@bankflow.test");
            var (key, _) = await EnrollMfa(owner);
            using var first = factory.CreateClient();
            using var second = factory.CreateClient();
            await SetCsrf(first); await SetCsrf(second);
            Assert.Equal(HttpStatusCode.Accepted, (await Login(first, AuthFactory.Password)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await Login(second, AuthFactory.Password)).StatusCode);
            gate.Enabled = true;
            var code = Code(key, 1);
            var responses = await Task.WhenAll(first.PostAsJsonAsync("/auth/mfa/verify", new { code }),
                second.PostAsJsonAsync("/auth/mfa/verify", new { code }));
            Assert.Single(responses.Where(response => response.StatusCode == HttpStatusCode.NoContent));
            Assert.Single(responses.Where(response => response.StatusCode == HttpStatusCode.Unauthorized));
        }
        finally
        {
            File.Delete(path); File.Delete(path + "-shm"); File.Delete(path + "-wal");
        }
    }

    private sealed class MfaCommitGate : SaveChangesInterceptor
    {
        public volatile bool Enabled;
        private int _arrivals;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<IdentityUserToken<string>>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.LoginProvider == "BankFlow.Mfa.Replay"))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task Mfa_ShouldFailClosedIfEnabledWithoutAnAuthenticatorKey()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByEmailAsync("operator@bankflow.test"))!;
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        }
        await SetCsrf(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, AuthFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/accounts")).StatusCode);
    }

    private static string Code(string key, int step = 0) =>
        new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(key)).ComputeTotp(DateTime.UtcNow.AddSeconds(step * 30));

    private static async Task<(string Key, string[] Recovery)> EnrollMfa(HttpClient client)
    {
        var setup = await client.PostAsJsonAsync("/auth/mfa/setup", new { password = AuthFactory.Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var data = await setup.Content.ReadFromJsonAsync<JsonElement>();
        var key = data.GetProperty("sharedKey").GetString()!;
        Assert.StartsWith("otpauth://totp/", data.GetProperty("authenticatorUri").GetString());
        await SetCsrf(client);
        var enabled = await client.PostAsJsonAsync("/auth/mfa/enable", new { code = Code(key) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        await SetCsrf(client);
        return (key, (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(code => code.GetString()!).ToArray());
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    [Fact]
    public async Task Login_ShouldRequireSecondFactorBeforeOpeningSession()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByEmailAsync("operator@bankflow.test"))!;
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        }
        await SetCsrf(client);
        var response = await Login(client, AuthFactory.Password);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("requiresMfa").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/accounts")).StatusCode);
    }

    [Fact]
    public async Task Clients_ShouldOnlySeeOwnAccountsAndCannotWriteOrManageClients()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient();
        await AuthenticateAs(operatorClient, "operator@bankflow.test");
        var firstOwner = await CreateClient(operatorClient, "first@bankflow.test");
        var secondOwner = await CreateClient(operatorClient, "second@bankflow.test");
        var firstAccount = await CreateOwnedAccount(operatorClient, firstOwner, "FIRST001");
        var secondAccount = await CreateOwnedAccount(operatorClient, secondOwner, "SECOND001");
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.PostAsJsonAsync($"/accounts/{firstAccount}/deposit", new { amount = 100 })).StatusCode);

        using var first = factory.CreateClient();
        await AuthenticateAs(first, "first@bankflow.test");
        var identity = await first.GetFromJsonAsync<JsonElement>("/auth/me");
        Assert.Equal("Client", identity.GetProperty("roles")[0].GetString());
        var accounts = await first.GetFromJsonAsync<JsonElement>("/accounts");
        Assert.Equal(1, accounts.GetArrayLength());
        Assert.Equal(firstAccount, accounts[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync($"/accounts/{firstAccount}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync($"/accounts/{firstAccount}/transactions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync($"/accounts/{secondAccount}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync($"/accounts/{secondAccount}/transactions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.GetAsync("/clients")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync("/clients", new { email = "forged@bankflow.test", password = AuthFactory.Password, role = "Operator" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync("/accounts", new { customerName = "Forged", accountNumber = "FORGED001", ownerUserId = firstOwner })).StatusCode);
        foreach (var id in new[] { firstAccount, secondAccount })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync($"/accounts/{id}/deposit", new { amount = 10 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync($"/accounts/{id}/withdraw", new { amount = 10 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await first.PutAsJsonAsync($"/accounts/{id}/owner", new { ownerUserId = firstOwner })).StatusCode);
        }

        using var second = factory.CreateClient();
        await AuthenticateAs(second, "second@bankflow.test");
        var visibleToSecond = await second.GetFromJsonAsync<JsonElement>("/accounts");
        Assert.Equal(1, visibleToSecond.GetArrayLength());
        Assert.Equal(secondAccount, visibleToSecond[0].GetProperty("id").GetGuid());
        var all = await operatorClient.GetFromJsonAsync<JsonElement>("/accounts");
        Assert.Equal(2, all.GetArrayLength());
    }

    [Fact]
    public async Task Operator_ShouldAssignLegacyAccountOnceAndRejectNonClientOwners()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient();
        await AuthenticateAs(operatorClient, "operator@bankflow.test");
        var owner = await CreateClient(operatorClient, "owner@bankflow.test");
        var legacy = new Account(Guid.NewGuid(), "Legacy holder", "LEGACY001");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
            db.Accounts.Add(legacy);
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await AuthenticateAs(client, "owner@bankflow.test");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/accounts/{legacy.Id}")).StatusCode);
        var me = await operatorClient.GetFromJsonAsync<JsonElement>("/auth/me");
        var operatorId = me.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await operatorClient.PutAsJsonAsync($"/accounts/{legacy.Id}/owner", new { ownerUserId = operatorId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await operatorClient.PutAsJsonAsync($"/accounts/{legacy.Id}/owner", new { ownerUserId = "missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.PutAsJsonAsync($"/accounts/{legacy.Id}/owner", new { ownerUserId = owner })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/accounts/{legacy.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await operatorClient.PutAsJsonAsync($"/accounts/{legacy.Id}/owner", new { ownerUserId = owner })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await operatorClient.PutAsJsonAsync($"/accounts/{Guid.NewGuid()}/owner", new { ownerUserId = owner })).StatusCode);
    }

    [Fact]
    public async Task Operator_ShouldRequireValidClientAndCreateOnlyClientRole()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient();
        await AuthenticateAs(operatorClient, "operator@bankflow.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await operatorClient.PostAsJsonAsync("/accounts", new { customerName = "Owner", accountNumber = "TEST0001" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await operatorClient.PostAsJsonAsync("/accounts", new { customerName = "Owner", accountNumber = "TEST0001", ownerUserId = "missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await operatorClient.PostAsJsonAsync("/clients", new { email = "invalid", password = AuthFactory.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await operatorClient.PostAsJsonAsync("/clients", new { email = "weak@bankflow.test", password = "weakpassword" })).StatusCode);
        var owner = await CreateClient(operatorClient, "registered@bankflow.test");
        Assert.Equal(HttpStatusCode.Conflict, (await operatorClient.PostAsJsonAsync("/clients", new { email = "registered@bankflow.test", password = AuthFactory.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.GetAsync($"/clients/{owner}")).StatusCode);
        using var registered = factory.CreateClient();
        await AuthenticateAs(registered, "registered@bankflow.test");
        var identity = await registered.GetFromJsonAsync<JsonElement>("/auth/me");
        Assert.Equal(1, identity.GetProperty("roles").GetArrayLength());
        Assert.Equal("Client", identity.GetProperty("roles")[0].GetString());
    }

    [Fact]
    public async Task AuthenticatedUserWithoutRole_ShouldHaveNoBankingAccess()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var result = await users.CreateAsync(new IdentityUser { UserName = "roleless@bankflow.test", Email = "roleless@bankflow.test" }, AuthFactory.Password);
            Assert.True(result.Succeeded);
        }
        await AuthenticateAs(client, "roleless@bankflow.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/clients")).StatusCode);
    }

    private static async Task AuthenticateAs(HttpClient client, string email)
    {
        await SetCsrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/auth/login", new { username = email, password = AuthFactory.Password })).StatusCode);
        await SetCsrf(client);
    }

    [Fact]
    public async Task Transfer_ShouldCommitBalancedMovementsAndReplayWithoutDuplicating()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient();
        var (sourceId, destinationId) = await PrepareTransfer(operatorClient);
        using var sender = factory.CreateClient();
        await AuthenticateAs(sender, "sender@bankflow.test");
        var key = Guid.NewGuid();
        var created = await SendTransfer(sender, sourceId, "TRANSFER002", 25m, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        var replay = await SendTransfer(sender, sourceId, "TRANSFER002", 25m, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.GetProperty("id").GetGuid(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await sender.GetAsync(created.Headers.Location)).StatusCode);
        var mismatch = await SendTransfer(sender, sourceId, "TRANSFER002", 26m, key);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal("idempotency_conflict", (await mismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        Assert.Equal(75m, (await db.Accounts.FindAsync(sourceId))!.Balance);
        Assert.Equal(25m, (await db.Accounts.FindAsync(destinationId))!.Balance);
        Assert.Equal(1, await db.Transfers.CountAsync());
        var movements = await db.Transactions.Where(transaction => transaction.TransferId != null).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(movements.Single(transaction => transaction.Type == "TransferOut").Amount,
            movements.Single(transaction => transaction.Type == "TransferIn").Amount);
        using var receiver = factory.CreateClient();
        await AuthenticateAs(receiver, "receiver@bankflow.test");
        Assert.Equal(HttpStatusCode.NotFound, (await receiver.GetAsync(created.Headers.Location)).StatusCode);
        var history = await receiver.GetFromJsonAsync<JsonElement>($"/accounts/{destinationId}/transactions");
        Assert.Equal("TransferIn", history[0].GetProperty("type").GetString());
        Assert.Equal(receipt.GetProperty("id").GetGuid(), history[0].GetProperty("transferId").GetGuid());
        Assert.Equal(HttpStatusCode.Created, (await SendTransfer(receiver, destinationId, "TRANSFER001", 10m, key)).StatusCode);
    }

    [Fact]
    public async Task Transfer_ShouldRejectInvalidRequestsWithoutChangingFunds()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient();
        var (sourceId, destinationId) = await PrepareTransfer(operatorClient);
        using var sender = factory.CreateClient();
        await AuthenticateAs(sender, "sender@bankflow.test");
        foreach (var amount in new[] { 0m, -1m, 0.001m, 1000001m })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendTransfer(sender, sourceId, "TRANSFER002", amount, Guid.NewGuid())).StatusCode);
        var insufficient = await SendTransfer(sender, sourceId, "TRANSFER002", 101m, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, insufficient.StatusCode);
        Assert.Equal("insufficient_funds", (await insufficient.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendTransfer(sender, sourceId, "TRANSFER001", 10m, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendTransfer(sender, sourceId, "MISSING001", 10m, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendTransfer(sender, destinationId, "TRANSFER001", 10m, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sender.PostAsJsonAsync("/transfers", new { sourceAccountId = sourceId, destinationAccountNumber = "TRANSFER002", amount = 10 })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        Assert.Equal(100m, (await db.Accounts.FindAsync(sourceId))!.Balance);
        Assert.Equal(0m, (await db.Accounts.FindAsync(destinationId))!.Balance);
        Assert.Equal(0, await db.Transfers.CountAsync());
    }

    [Fact]
    public async Task Transfer_ShouldRollbackBothBalancesAndReceiptWhenCreditFails()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        var (sourceId, destinationId) = await PrepareTransfer(client);
        using (var setup = factory.Services.CreateScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<BankFlowDbContext>();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailTransferCredit BEFORE INSERT ON Transactions WHEN NEW.Type = 'TransferIn' BEGIN SELECT RAISE(ABORT, 'Injected credit failure'); END;");
        }
        Assert.Equal(HttpStatusCode.InternalServerError, (await SendTransfer(client, sourceId, "TRANSFER002", 25m, Guid.NewGuid())).StatusCode);
        using var scope = factory.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        Assert.Equal(100m, (await stored.Accounts.FindAsync(sourceId))!.Balance);
        Assert.Equal(0m, (await stored.Accounts.FindAsync(destinationId))!.Balance);
        Assert.Equal(0, await stored.Transfers.CountAsync());
        Assert.Equal(1, await stored.Transactions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transfer_ShouldResolveInterleavedWritesWithoutDoubleSpending(bool sameKey)
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        var (sourceId, destinationId) = await PrepareTransfer(client);
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ITransferUnitOfWork>();
        var second = secondScope.ServiceProvider.GetRequiredService<ITransferUnitOfWork>();
        var firstSource = (await first.GetAccountAsync(sourceId, default))!;
        var secondSource = (await second.GetAccountAsync(sourceId, default))!;
        var firstDestination = (await first.GetAccountAsync(destinationId, default))!;
        var secondDestination = (await second.GetAccountAsync(destinationId, default))!;
        var key = Guid.NewGuid();
        var firstTransfer = new Transfer(firstSource, firstDestination, firstSource.OwnerUserId!, key, 70m, "");
        var secondTransfer = new Transfer(secondSource, secondDestination, secondSource.OwnerUserId!, sameKey ? key : Guid.NewGuid(), 70m, "");
        first.Add(firstTransfer, new Transaction(Guid.NewGuid(), sourceId, 70m, "TransferOut", "Sent", firstTransfer.Id),
            new Transaction(Guid.NewGuid(), destinationId, 70m, "TransferIn", "Received", firstTransfer.Id));
        second.Add(secondTransfer, new Transaction(Guid.NewGuid(), sourceId, 70m, "TransferOut", "Sent", secondTransfer.Id),
            new Transaction(Guid.NewGuid(), destinationId, 70m, "TransferIn", "Received", secondTransfer.Id));
        await first.CommitAsync(firstTransfer, default);
        if (sameKey)
        {
            var replay = await second.CommitAsync(secondTransfer, default);
            Assert.True(replay.Replayed);
            Assert.Equal(firstTransfer.Id, replay.Transfer.Id);
        }
        else
        {
            var conflict = await Assert.ThrowsAsync<TransferConflictException>(() => second.CommitAsync(secondTransfer, default));
            Assert.Equal("balance_changed", conflict.Code);
        }
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        Assert.Equal(30m, (await db.Accounts.FindAsync(sourceId))!.Balance);
        Assert.Equal(70m, (await db.Accounts.FindAsync(destinationId))!.Balance);
        Assert.Equal(1, await db.Transfers.CountAsync());
        Assert.Equal(3, await db.Transactions.CountAsync());
    }

    private static async Task<(Guid Source, Guid Destination)> PrepareTransfer(HttpClient client)
    {
        await AuthenticateAs(client, "operator@bankflow.test");
        var sender = await CreateClient(client, "sender@bankflow.test");
        var receiver = await CreateClient(client, "receiver@bankflow.test");
        var source = await CreateOwnedAccount(client, sender, "TRANSFER001");
        var destination = await CreateOwnedAccount(client, receiver, "TRANSFER002");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/accounts/{source}/deposit", new { amount = 100 })).StatusCode);
        return (source, destination);
    }

    [Fact]
    public void Transfer_ShouldRejectDestinationOverflowBeforeDebiting()
    {
        var source = new Account(Guid.NewGuid(), "Sender", "SOURCE01", "sender");
        var destination = new Account(Guid.NewGuid(), "Receiver", "DEST0001", "receiver");
        source.Deposit(100m);
        destination.Deposit(9999999999999999.99m);
        Assert.Throws<InvalidOperationException>(() => new Transfer(source, destination, "sender", Guid.NewGuid(), 10m, ""));
        Assert.Equal(100m, source.Balance);
        Assert.Equal(9999999999999999.99m, destination.Balance);
    }

    private static Task<HttpResponseMessage> SendTransfer(HttpClient client, Guid source, string destination, decimal amount, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/transfers")
        {
            Content = JsonContent.Create(new { sourceAccountId = source, destinationAccountNumber = destination, amount, description = "" })
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return client.SendAsync(request);
    }

    [Fact]
    public async Task AccountBalance_ShouldRejectConcurrentWithdrawals()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        var account = new Account(Guid.NewGuid(), "Concurrent holder", "BALANCE001");
        account.Deposit(100m);
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<BankFlowDbContext>();
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        var firstCopy = await firstDb.Accounts.SingleAsync(candidate => candidate.Id == account.Id);
        var secondCopy = await secondDb.Accounts.SingleAsync(candidate => candidate.Id == account.Id);
        firstCopy.Withdraw(50m);
        secondCopy.Withdraw(70m);
        await firstDb.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
    }

    [Fact]
    public async Task AccountOwner_ShouldRejectConcurrentAssignments()
    {
        await using var factory = new AuthFactory();
        using var client = factory.CreateClient();
        var legacy = new Account(Guid.NewGuid(), "Legacy holder", "CONCURRENT001");
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<BankFlowDbContext>();
            db.Accounts.Add(legacy);
            await db.SaveChangesAsync();
        }
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        var firstCopy = await firstDb.Accounts.SingleAsync(account => account.Id == legacy.Id);
        var secondCopy = await secondDb.Accounts.SingleAsync(account => account.Id == legacy.Id);
        firstCopy.AssignOwner("first-client");
        secondCopy.AssignOwner("second-client");
        await firstDb.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
    }

    private static async Task<string> CreateClient(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/clients", new { email, password = AuthFactory.Password, role = "Operator" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<Guid> CreateOwnedAccount(HttpClient client, string ownerUserId, string number)
    {
        var response = await client.PostAsJsonAsync("/accounts", new { customerName = "Test holder", accountNumber = number, ownerUserId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Notifications_ShouldBePrivateDurableAndNegotiationShouldAllowAuthenticatedSignalR()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient(); await AuthenticateAs(operatorClient, "operator@bankflow.test");
        var senderId = await CreateClient(operatorClient, "notification-sender@bankflow.test");
        var receiverId = await CreateClient(operatorClient, "notification-receiver@bankflow.test");
        var source = await CreateOwnedAccount(operatorClient, senderId, "NOTICE001");
        await CreateOwnedAccount(operatorClient, receiverId, "NOTICE002");
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.PostAsJsonAsync($"/accounts/{source}/deposit", new { amount = 100 })).StatusCode);
        var key = Guid.NewGuid();
        var transfer = await SendTransfer(operatorClient, source, "NOTICE002", 25m, key);
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
            var publisher = new SqlOutboxPublisher(db, TimeProvider.System, new NoopNotificationSink());
            Assert.Equal(2, await publisher.ProcessBatchAsync(10, default));
        }
        using var receiver = factory.CreateClient(); await AuthenticateAs(receiver, "notification-receiver@bankflow.test");
        var inbox = await receiver.GetFromJsonAsync<JsonElement>("/notifications");
        Assert.Equal(1, inbox.GetProperty("totalCount").GetInt32()); Assert.Equal(1, inbox.GetProperty("unreadCount").GetInt32());
        var payload = JsonDocument.Parse(inbox.GetProperty("items")[0].GetProperty("payload").GetString()!);
        Assert.Equal("E001", payload.RootElement.GetProperty("counterpartyLastFour").GetString());
        Assert.False(payload.RootElement.TryGetProperty("sourceAccountNumber", out _));
        var notificationId = inbox.GetProperty("items")[0].GetProperty("id").GetGuid();
        await SetCsrf(receiver);
        Assert.Equal(HttpStatusCode.NoContent, (await receiver.PostAsJsonAsync("/notifications/read", new { id = notificationId })).StatusCode);
        Assert.Equal(0, (await receiver.GetFromJsonAsync<JsonElement>("/notifications")).GetProperty("unreadCount").GetInt32());
        using var sender = factory.CreateClient(); await AuthenticateAs(sender, "notification-sender@bankflow.test");
        var senderInbox = await sender.GetFromJsonAsync<JsonElement>("/notifications");
        Assert.Equal(1, senderInbox.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await sender.PostAsJsonAsync("/notifications/read", new { id = notificationId })).StatusCode);
        var negotiate = await receiver.PostAsync("/hubs/notifications/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/notifications")).StatusCode);
    }

    [Fact]
    public async Task NotificationHub_NegotiationMustAuthenticateAndRejectsBadOrigins()
    {
        await using var factory = new AuthFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/notifications/negotiate?negotiateVersion=1", new StringContent(""))).StatusCode);
        using var authenticated = factory.CreateClient();
        await AuthenticateAs(authenticated, "operator@bankflow.test");
        var valid = await authenticated.PostAsync("/hubs/notifications/negotiate?negotiateVersion=1", new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.True(NotificationHub.IsAllowedOrigin("http://localhost:4200", "http", "localhost:4200"));
        Assert.False(NotificationHub.IsAllowedOrigin("https://attacker.example", "http", "localhost:4200"));
        Assert.False(NotificationHub.IsAllowedOrigin("http://localhost.evil:4200", "http", "localhost:4200"));
        Assert.NotEqual(NotificationHub.Group("user", "old-stamp"), NotificationHub.Group("user", "new-stamp"));
    }

    private sealed class NoopNotificationSink : INotificationRealtimeSink
    {
        public Task NotifyAvailableAsync(string recipientUserId, Guid notificationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Payments_ShouldProtectBeneficiariesAndSourceOwnershipAndAllowCancellation()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient();
        var (source, destination) = await PrepareTransfer(operatorClient);
        using var sender = factory.CreateClient();
        await AuthenticateAs(sender, "sender@bankflow.test");
        var response = await sender.PostAsJsonAsync("/beneficiaries", new { name = "Receiver", accountNumber = "TRANSFER002" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var beneficiary = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await sender.PostAsJsonAsync("/beneficiaries", new { name = "Duplicate", accountNumber = "TRANSFER002" })).StatusCode);
        var due = DateTimeOffset.UtcNow.AddHours(1);
        sender.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var created = await sender.PostAsJsonAsync("/scheduled-payments", new { sourceAccountId = source, beneficiaryId = beneficiary, amount = 10, frequency = 0, dueAt = due });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var replay = await sender.PostAsJsonAsync("/scheduled-payments", new { sourceAccountId = source, beneficiaryId = beneficiary, amount = 10, frequency = 0, dueAt = due });
        Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await sender.PostAsJsonAsync("/scheduled-payments", new { sourceAccountId = source, beneficiaryId = beneficiary, amount = 20, frequency = 0, dueAt = due })).StatusCode);
        var listed = await sender.GetFromJsonAsync<JsonElement>("/scheduled-payments");
        Assert.Equal(1, listed.GetProperty("totalCount").GetInt32());
        Assert.Equal("TRANSFER001", listed.GetProperty("items")[0].GetProperty("sourceAccountNumber").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await sender.PostAsJsonAsync("/scheduled-payments", new { sourceAccountId = destination, beneficiaryId = beneficiary, amount = 10, frequency = 0, dueAt = due })).StatusCode);
        using var other = factory.CreateClient();
        await AuthenticateAs(other, "receiver@bankflow.test");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/beneficiaries/{beneficiary}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/beneficiaries/{beneficiary}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/scheduled-payments/{id}/cancel", new { })).StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/scheduled-payments")).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await sender.PostAsJsonAsync("/scheduled-payments", new { sourceAccountId = source, beneficiaryId = beneficiary, amount = 0.001m, frequency = 0, dueAt = due })).StatusCode);
        sender.DefaultRequestHeaders.Remove("Idempotency-Key"); sender.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await sender.PostAsJsonAsync("/scheduled-payments", new { sourceAccountId = source, beneficiaryId = beneficiary, amount = 10, frequency = 0, dueAt = DateTimeOffset.UtcNow.AddDays(-1) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sender.PostAsJsonAsync($"/scheduled-payments/{id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sender.PostAsJsonAsync($"/scheduled-payments/{id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sender.DeleteAsync($"/beneficiaries/{beneficiary}")).StatusCode);
    }

    [Fact]
    public async Task Pages_ShouldBeBoundedStableSearchableAndOwnerIsolated()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient();
        await AuthenticateAs(operatorClient, "operator@bankflow.test");
        var firstOwner = await CreateClient(operatorClient, "page-owner@bankflow.test");
        var otherOwner = await CreateClient(operatorClient, "page-other@bankflow.test");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
            for (var index = 0; index < 25; index++)
                db.Accounts.Add(new Account(Guid.NewGuid(), "Page holder", $"PAGE{index:D4}", firstOwner));
            db.Accounts.Add(new Account(Guid.NewGuid(), "Other holder", "FOREIGN01", otherOwner));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await AuthenticateAs(client, "page-owner@bankflow.test");
        var first = await client.GetFromJsonAsync<JsonElement>("/accounts/page?pageSize=10");
        var second = await client.GetFromJsonAsync<JsonElement>("/accounts/page?page=2&pageSize=10");
        Assert.Equal(25, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        var firstIds = first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.DoesNotContain(second.GetProperty("items").EnumerateArray(), item => firstIds.Contains(item.GetProperty("id").GetGuid()));
        var repeated = await client.GetFromJsonAsync<JsonElement>("/accounts/page?pageSize=10");
        Assert.Equal(firstIds, repeated.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray());
        var searched = await client.GetFromJsonAsync<JsonElement>("/accounts/page?search=PAGE0003");
        Assert.Equal(1, searched.GetProperty("items").GetArrayLength());
        var invisible = await client.GetFromJsonAsync<JsonElement>("/accounts/page?search=FOREIGN01");
        Assert.Equal(0, invisible.GetProperty("totalCount").GetInt32());
        foreach (var query in new[] { "page=0", "page=10001", "pageSize=0", "pageSize=101", "search=" + new string('x', 101) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/accounts/page?" + query)).StatusCode);
        var last = await client.GetFromJsonAsync<JsonElement>("/accounts/page?page=4&pageSize=10");
        Assert.Equal(0, last.GetProperty("items").GetArrayLength());
        var all = await operatorClient.GetFromJsonAsync<JsonElement>("/accounts/page?pageSize=100");
        Assert.Equal(26, all.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task MovementPages_ShouldRejectForeignAccountsAndAvoidTracking()
    {
        await using var factory = new AuthFactory();
        using var operatorClient = factory.CreateClient();
        var (source, destination) = await PrepareTransfer(operatorClient);
        for (var index = 0; index < 5; index++)
            await operatorClient.PostAsJsonAsync($"/accounts/{source}/deposit", new { amount = 1 });
        using var sender = factory.CreateClient();
        await AuthenticateAs(sender, "sender@bankflow.test");
        var page = await sender.GetFromJsonAsync<JsonElement>($"/accounts/{source}/transactions/page?pageSize=2");
        Assert.Equal(6, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await sender.GetAsync($"/accounts/{destination}/transactions/page")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
        var queries = scope.ServiceProvider.GetRequiredService<IBankingReadQueries>();
        await queries.AccountsAsync(1, 10, null, null);
        await queries.MovementsAsync(source, 1, 2, null);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static async Task SetCsrf(HttpClient client)
    {
        var response = await client.GetAsync("/auth/csrf");
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("XSRF-TOKEN="));
        var token = Uri.UnescapeDataString(cookie.Split(';')[0]["XSRF-TOKEN=".Length..]);
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
    }

    private sealed class AuthFactory(TimeProvider? clock = null, string? authConnection = null, SaveChangesInterceptor? interceptor = null) : WebApplicationFactory<Program>
    {
        public const string Password = "TestingOnly123!";
        private readonly SqliteConnection _auth = new("Data Source=:memory:");
        private readonly SqliteConnection _bank = new("Data Source=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _auth.Open();
            _bank.Open();
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bootstrap:Email"] = "operator@bankflow.test",
                ["Bootstrap:Password"] = Password,
                ["Payments:WorkerEnabled"] = "false",
                ["Notifications:WorkerEnabled"] = "false"
            }));
            builder.ConfigureServices(services =>
            {
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<DbContextOptions<BankFlowDbContext>>();
                services.AddDbContext<AuthDbContext>(options =>
                {
                    if (authConnection is null) options.UseSqlite(_auth);
                    else options.UseSqlite(authConnection);
                    if (interceptor is not null) options.AddInterceptors(interceptor);
                });
                services.AddDbContext<BankFlowDbContext>(options => options.UseSqlite(_bank));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _auth.DisposeAsync();
            await _bank.DisposeAsync();
        }
    }
}