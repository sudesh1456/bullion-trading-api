using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BullionTrading.Api.Auth;
using BullionTrading.Api.Data;
using BullionTrading.Api.Domain;
using BullionTrading.Api.Hubs;
using BullionTrading.Api.Rates;
using BullionTrading.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- Options -------------------------------------------------------------------
builder.Services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section))
    .Validate(o => o.SigningKey.Length >= 32, "Jwt:SigningKey must be at least 32 characters")
    .ValidateOnStart();
builder.Services.Configure<RateFeedOptions>(config.GetSection(RateFeedOptions.Section));
builder.Services.Configure<TradingOptions>(config.GetSection(TradingOptions.Section));

// --- Infrastructure --------------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(config.GetConnectionString("Default") ?? "Data Source=bullion.db"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, SubClaimUserIdProvider>();
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimits:LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});

// --- Auth ------------------------------------------------------------------------
builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>, TimeProvider>((o, jwt, clock) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = jwt.Value.Audience,
            IssuerSigningKey = jwt.Value.Key(),
            NameClaimType = Claims.Name,
            RoleClaimType = Claims.Role,
            // Validate expiry against the app's TimeProvider (same clock that issued the token).
            LifetimeValidator = (notBefore, expires, _, p) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore <= now + p.ClockSkew) && (expires is null || expires >= now - p.ClockSkew);
            },
        };
        // Browsers can't set headers on WebSocket upgrades, so SignalR sends the token in the query string.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.HttpContext.Request.Path.StartsWithSegments(RatesHub.Path) && ctx.Request.Query.TryGetValue("access_token", out var token))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// --- Trading engine --------------------------------------------------------------
builder.Services.AddSingleton<RateBook>();
builder.Services.AddScoped<QuoteService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddSingleton<ITradeNotifier, SignalRTradeNotifier>();
builder.Services.AddHostedService<TickProcessor>();
if (config.GetValue(RateFeedOptions.Section + ":Simulated", true))
    builder.Services.AddHostedService<SimulatedRateFeed>();

var app = builder.Build();

// Map domain rule violations to RFC 7807 problem responses.
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var error = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = error is DomainException de ? (de.StatusCode, de.Message) : (500, "An unexpected error occurred");
    ctx.Response.StatusCode = status;
    await Results.Problem(statusCode: status, title: title).ExecuteAsync(ctx);
}));

await using (var scope = app.Services.CreateAsyncScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Bullion Trading API"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<RatesHub>(RatesHub.Path);
app.MapHealthChecks("/health");

app.Run();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
