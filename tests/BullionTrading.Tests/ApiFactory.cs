using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BullionTrading.Api.Contracts;
using BullionTrading.Api.Domain;
using BullionTrading.Api.Rates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BullionTrading.Tests;

/// <summary>
/// Boots the real API in-memory with a private SQLite database, the simulated
/// feed switched off (tests publish ticks explicitly) and a controllable clock.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _connectionString = $"Data Source=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;
    private readonly Dictionary<string, string> _tokens = [];

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public ApiFactory()
    {
        // An in-memory SQLite database lives only while a connection is open.
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("RateFeed:Simulated", "false");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-0123456789-abcdefghijklmnop");
        builder.UseSetting("RateLimits:LoginPerMinute", "1000");
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
    }

    public RateBook Rates => Services.GetRequiredService<RateBook>();

    public void PublishSpot(Metal metal, decimal price) => Rates.Publish(new SpotTick(metal, price, Clock.GetUtcNow()));

    public async Task<string> TokenFor(string email, string password)
    {
        if (_tokens.TryGetValue(email, out var cached)) return cached;
        var res = await CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Json);
        res.EnsureSuccessStatusCode();
        var login = await res.Content.ReadFromJsonAsync<LoginResponse>(Json);
        return _tokens[email] = login!.AccessToken;
    }

    public async Task<HttpClient> TraderClient() => Authorized(await TokenFor("trader@demo.test", "Trader@123"));

    public async Task<HttpClient> AdminClient() => Authorized(await TokenFor("admin@demo.test", "Admin@123"));

    private HttpClient Authorized(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Polls until <paramref name="condition"/> holds; the tick processor runs asynchronously.</summary>
    public static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var value = await probe();
            if (condition(value) || DateTime.UtcNow > deadline) return value;
            await Task.Delay(50);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _keepAlive.Dispose();
    }
}
