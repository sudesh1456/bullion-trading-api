using System.Net;
using System.Net.Http.Json;
using BullionTrading.Api.Contracts;
using BullionTrading.Api.Domain;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace BullionTrading.Tests;

public class TradingApiTests : IClassFixture<ApiFactory>
{
    private const int Gold10g = 2; // seeded "Gold 24K 999 Bar 10 g": premium 2%, discount 1%
    private readonly ApiFactory _api;

    public TradingApiTests(ApiFactory api)
    {
        _api = api;
        _api.PublishSpot(Metal.Gold, 10_000m);
        _api.PublishSpot(Metal.Silver, 100m);
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static HttpRequestMessage ExecuteRequest(Guid quoteId, string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new ExecuteQuoteRequest(quoteId), options: ApiFactory.Json) };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    private static async Task<T> Read<T>(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;

    private async Task<QuoteDto> Quote(HttpClient client, Side side = Side.Buy, int qty = 2)
    {
        var res = await client.PostAsJsonAsync("/api/quotes", new QuoteRequest(Gold10g, side, qty), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await Read<QuoteDto>(res);
    }

    // --- auth -------------------------------------------------------------------

    [Fact]
    public async Task Login_rejects_wrong_password()
    {
        var res = await _api.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("trader@demo.test", "nope"), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Trading_endpoints_require_authentication()
    {
        var res = await _api.CreateClient().PostAsJsonAsync("/api/quotes", new QuoteRequest(Gold10g, Side.Buy, 1), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // --- market data ------------------------------------------------------------

    [Fact]
    public async Task Products_are_priced_from_the_live_spot_rate()
    {
        var products = await _api.CreateClient().GetFromJsonAsync<List<ProductDto>>("/api/products", ApiFactory.Json);
        var bar = products!.Single(p => p.Id == Gold10g);
        Assert.Equal(102_000m, bar.BuyPrice);
        Assert.Equal(99_000m, bar.SellPrice);
    }

    // --- quotes & market orders -------------------------------------------------

    [Fact]
    public async Task Executing_a_quote_fills_at_the_locked_price_even_if_the_market_moves()
    {
        var client = await _api.TraderClient();
        var quote = await Quote(client);

        _api.PublishSpot(Metal.Gold, 12_000m); // market jumps 20% before the customer confirms
        var res = await client.SendAsync(ExecuteRequest(quote.QuoteId, NewKey()));
        _api.PublishSpot(Metal.Gold, 10_000m);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var order = await Read<OrderDto>(res);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(quote.UnitPrice, order.FilledPrice);
        Assert.Equal(quote.Total, order.Total);
    }

    [Fact]
    public async Task Retrying_with_the_same_idempotency_key_does_not_buy_twice()
    {
        var client = await _api.TraderClient();
        var quote = await Quote(client);
        var key = NewKey();

        var first = await client.SendAsync(ExecuteRequest(quote.QuoteId, key));
        var retry = await client.SendAsync(ExecuteRequest(quote.QuoteId, key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await Read<OrderDto>(first)).Id, (await Read<OrderDto>(retry)).Id);
    }

    [Fact]
    public async Task A_quote_can_only_be_used_once()
    {
        var client = await _api.TraderClient();
        var quote = await Quote(client);

        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(ExecuteRequest(quote.QuoteId, NewKey()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(ExecuteRequest(quote.QuoteId, NewKey()))).StatusCode);
    }

    [Fact]
    public async Task Executing_requires_an_idempotency_key()
    {
        var client = await _api.TraderClient();
        var quote = await Quote(client);
        var res = await client.SendAsync(ExecuteRequest(quote.QuoteId, key: null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Expired_quotes_are_rejected()
    {
        var client = await _api.TraderClient();
        var quote = await Quote(client);

        _api.Clock.Advance(TimeSpan.FromSeconds(31));
        var res = await client.SendAsync(ExecuteRequest(quote.QuoteId, NewKey()));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    // --- limit orders -----------------------------------------------------------

    [Fact]
    public async Task Buy_limit_order_fills_when_the_price_drops_to_the_limit()
    {
        var client = await _api.TraderClient();
        // Current buy price is 102,000; bid 1% lower.
        var res = await client.PostAsJsonAsync("/api/orders/limit", new LimitOrderRequest(Gold10g, Side.Buy, 1, 100_980m, null), ApiFactory.Json);
        var placed = await Read<OrderDto>(res);
        Assert.Equal(OrderStatus.Pending, placed.Status);

        _api.PublishSpot(Metal.Gold, 9_900m); // buy price → 100,980

        var filled = await ApiFactory.Eventually(
            () => client.GetFromJsonAsync<OrderDto>($"/api/orders/{placed.Id}", ApiFactory.Json),
            o => o!.Status == OrderStatus.Filled);
        _api.PublishSpot(Metal.Gold, 10_000m);

        Assert.Equal(OrderStatus.Filled, filled!.Status);
        Assert.Equal(100_980m, filled.FilledPrice);
    }

    [Fact]
    public async Task Marketable_limit_orders_fill_immediately()
    {
        var client = await _api.TraderClient();
        var res = await client.PostAsJsonAsync("/api/orders/limit", new LimitOrderRequest(Gold10g, Side.Buy, 1, 200_000m, null), ApiFactory.Json);
        var order = await Read<OrderDto>(res);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(102_000m, order.FilledPrice); // price improvement: fills at market, not at the limit
    }

    [Fact]
    public async Task Pending_orders_can_be_cancelled_once()
    {
        var client = await _api.TraderClient();
        var placed = await Read<OrderDto>(await client.PostAsJsonAsync("/api/orders/limit", new LimitOrderRequest(Gold10g, Side.Buy, 1, 1m, null), ApiFactory.Json));

        var cancel = await client.DeleteAsync($"/api/orders/{placed.Id}");
        var again = await client.DeleteAsync($"/api/orders/{placed.Id}");

        Assert.Equal(OrderStatus.Cancelled, (await Read<OrderDto>(cancel)).Status);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Limit_orders_expire()
    {
        var client = await _api.TraderClient();
        var expires = _api.Clock.GetUtcNow().AddMinutes(5);
        var placed = await Read<OrderDto>(await client.PostAsJsonAsync("/api/orders/limit", new LimitOrderRequest(Gold10g, Side.Buy, 1, 1m, expires), ApiFactory.Json));

        _api.Clock.Advance(TimeSpan.FromMinutes(6));
        _api.PublishSpot(Metal.Gold, 10_000m);

        var order = await ApiFactory.Eventually(
            () => client.GetFromJsonAsync<OrderDto>($"/api/orders/{placed.Id}", ApiFactory.Json),
            o => o!.Status == OrderStatus.Expired);
        Assert.Equal(OrderStatus.Expired, order!.Status);
    }

    // --- admin ------------------------------------------------------------------

    [Fact]
    public async Task Traders_cannot_use_admin_endpoints()
    {
        var client = await _api.TraderClient();
        var res = await client.GetAsync("/api/admin/orders");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_can_change_premiums_which_reprices_products()
    {
        var admin = await _api.AdminClient();
        var res = await admin.PutAsJsonAsync("/api/admin/products/1/pricing", new UpdatePricingRequest(10m, 5m, true), ApiFactory.Json);
        var product = await Read<ProductDto>(res);

        Assert.Equal(11_000m, product.BuyPrice); // 1 g coin at 10,000 + 10%
        Assert.Equal(9_500m, product.SellPrice);
    }

    [Fact]
    public async Task Admin_stats_include_todays_turnover()
    {
        var trader = await _api.TraderClient();
        var quote = await Quote(trader, Side.Sell, qty: 1);
        await trader.SendAsync(ExecuteRequest(quote.QuoteId, NewKey()));

        var stats = await (await _api.AdminClient()).GetFromJsonAsync<DailyStatsDto>("/api/admin/stats/today", ApiFactory.Json);
        Assert.True(stats!.SellTurnover >= quote.Total);
        Assert.Contains(stats.ByMetal, m => m.Metal == Metal.Gold);
    }

    // --- alerts + realtime ------------------------------------------------------

    [Fact]
    public async Task Rate_alert_is_pushed_over_signalr_when_the_price_crosses()
    {
        var client = await _api.TraderClient();
        var token = await _api.TokenFor("trader@demo.test", "Trader@123");

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_api.Server.BaseAddress, "hubs/rates"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _api.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = ApiFactory.Json)
            .Build();
        var received = new TaskCompletionSource<AlertDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<AlertDto>("AlertTriggered", a => received.TrySetResult(a));
        await hub.StartAsync();

        var created = await Read<AlertDto>(await client.PostAsJsonAsync("/api/alerts", new CreateAlertRequest(Metal.Silver, AlertDirection.Above, 150m), ApiFactory.Json));
        _api.PublishSpot(Metal.Silver, 151m);

        var alert = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _api.PublishSpot(Metal.Silver, 100m);

        Assert.Equal(created.Id, alert.Id);
        Assert.Equal(AlertStatus.Triggered, alert.Status);
        Assert.Equal(151m, alert.TriggeredPrice);
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        var res = await _api.CreateClient().GetAsync("/health");
        Assert.Equal("Healthy", await res.Content.ReadAsStringAsync());
    }
}
