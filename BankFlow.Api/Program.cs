using BankFlow.Api.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using BankFlow.Api;
using BankFlow.Api.Filters;
using BankFlow.Application.Interfaces;
using BankFlow.Application.Services;
using BankFlow.Infrastructure.Persistence;
using BankFlow.Infrastructure.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();

builder.Services.AddDbContext<BankFlowDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("BankFlowDb") ?? "Server=(localdb)\\mssqllocaldb;Database=BankFlow;Trusted_Connection=True;TrustServerCertificate=True;"));

builder.Services.AddScoped<IAccountRepository, SqlAccountRepository>();
builder.Services.AddScoped<ITransactionRepository, SqlTransactionRepository>();
builder.Services.AddScoped<BankingService>();
builder.Services.AddScoped<ITransferUnitOfWork, SqlTransferUnitOfWork>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<IBankingReadQueries, SqlBankingReadQueries>();
builder.Services.AddScoped<IPaymentRepository, SqlPaymentRepository>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<INotificationStore, SqlNotificationStore>();
builder.Services.AddScoped<IOutboxPublisher, SqlOutboxPublisher>();
builder.Services.AddScoped<INotificationRealtimeSink, SignalRNotificationSink>();
builder.Services.AddScoped<IPaymentExecutionAuthorizer, IdentityPaymentAuthorizer>();
builder.Services.AddScoped<IScheduledPaymentRunner, SqlScheduledPaymentRunner>();
if (builder.Configuration.GetValue("Payments:WorkerEnabled", true))
    builder.Services.AddHostedService<ScheduledPaymentWorker>();
if (builder.Configuration.GetValue("Notifications:WorkerEnabled", true))
    builder.Services.AddHostedService<NotificationOutboxWorker>();

builder.Services.AddValidatorsFromAssembly(typeof(CreateAccountRequestValidator).Assembly);

builder.AddBankFlowAuthentication();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(BankFlowRoles.BankingPolicy, policy => policy.RequireRole(BankFlowRoles.Operator, BankFlowRoles.Client).RequireClaim(ClaimTypes.NameIdentifier));
    options.AddPolicy(BankFlowRoles.OperatorPolicy, policy => policy.RequireRole(BankFlowRoles.Operator));
});

var app = builder.Build();
await app.InitializeAuthenticationAsync();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BankFlowDbContext>();
    await BankFlowSchemaInitializer.InitializeAsync(db);
}

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

        context.Response.StatusCode = exception switch
        {
            DbUpdateConcurrencyException => StatusCodes.Status409Conflict,
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => StatusCodes.Status409Conflict,
            DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: 1555 or 2067 } } => StatusCodes.Status409Conflict,
            ArgumentException => StatusCodes.Status400BadRequest,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            InvalidOperationException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        await context.Response.WriteAsJsonAsync(new
        {
            error = context.Response.StatusCode == 500 ? "Unexpected error." : exception?.Message,
            statusCode = context.Response.StatusCode
        });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
        HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
    {
        if (!context.Request.Path.StartsWithSegments("/hubs/notifications"))
        {
            try
            {
                await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid CSRF token." });
                return;
            }
        }
    }
    await next(context);
});
app.MapBankFlowAuthentication();
app.MapMfa();
app.MapClients();
app.MapAccounts();
app.MapTransfers();
app.MapPayments();
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapNotifications();

app.Run();

public partial class Program;
