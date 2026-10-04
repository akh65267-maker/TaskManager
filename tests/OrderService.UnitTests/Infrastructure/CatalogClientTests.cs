using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using OrderService.Application.Orders;
using OrderService.Infrastructure.Catalog;

namespace OrderService.UnitTests.Infrastructure;

public class CatalogClientTests
{
    private static CatalogClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://catalog/") },
            Mock.Of<ILogger<CatalogClient>>());

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetPricesAsync_ReadsThePriceOfEachProduct()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var client = ClientFor(req => req.RequestUri!.AbsolutePath.EndsWith(a.ToString())
            ? Json($$"""{"id":"{{a}}","name":"A","description":"","price":12.5,"category":"x"}""")
            : Json($$"""{"id":"{{b}}","name":"B","description":"","price":3,"category":"x"}"""));

        var prices = await client.GetPricesAsync(new[] { a, b });

        Assert.Equal(12.5m, prices[a]);
        Assert.Equal(3m, prices[b]);
    }

    [Fact]
    public async Task GetPricesAsync_LeavesOutAProductTheCatalogDoesNotKnow()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var client = ClientFor(req => req.RequestUri!.AbsolutePath.EndsWith(unknown.ToString())
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("""{"price":5}"""));

        var prices = await client.GetPricesAsync(new[] { known, unknown });

        Assert.True(prices.ContainsKey(known));
        Assert.False(prices.ContainsKey(unknown));
    }

    [Fact]
    public async Task GetPricesAsync_RequestsADuplicatedProductOnce()
    {
        var id = Guid.NewGuid();
        var requests = 0;
        var client = ClientFor(_ => { Interlocked.Increment(ref requests); return Json("""{"price":5}"""); });

        await client.GetPricesAsync(new[] { id, id });

        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetPricesAsync_WhenTheCatalogFails_IsReportedAsUnavailable_NotAsUnknown(HttpStatusCode status)
    {
        var client = ClientFor(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<OrderRejectedException>(() => client.GetPricesAsync(new[] { Guid.NewGuid() }));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task GetPricesAsync_WhenTheCatalogCannotBeReached_IsReportedAsUnavailable()
    {
        var client = ClientFor(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<OrderRejectedException>(() => client.GetPricesAsync(new[] { Guid.NewGuid() }));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task GetPricesAsync_WhenTheCatalogTimesOut_IsReportedAsUnavailable()
    {
        var client = ClientFor(_ => throw new TaskCanceledException("timed out"));

        var ex = await Assert.ThrowsAsync<OrderRejectedException>(() => client.GetPricesAsync(new[] { Guid.NewGuid() }));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
