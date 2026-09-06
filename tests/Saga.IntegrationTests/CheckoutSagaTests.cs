extern alias InventoryAlias;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using OrderService.Application.Orders;
using OrderService.Domain;
using OrderService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

using InventoryDbContext = InventoryAlias::InventoryService.Infrastructure.Persistence.InventoryDbContext;
using InventoryItem = InventoryAlias::InventoryService.Domain.InventoryItem;
using IInventoryRepository = InventoryAlias::InventoryService.Domain.IInventoryRepository;
using InventoryRepository = InventoryAlias::InventoryService.Infrastructure.Persistence.InventoryRepository;
using ReserveStockConsumer = InventoryAlias::InventoryService.Application.Consumers.ReserveStockConsumer;
using ReleaseStockConsumer = InventoryAlias::InventoryService.Application.Consumers.ReleaseStockConsumer;

namespace Saga.IntegrationTests;

/// <summary>
/// Exercises the real checkout saga end to end: a real Postgres per service,
/// a real RabbitMQ, OrderService hosted via WebApplicationFactory (its HTTP
/// API + saga), and InventoryService's consumers hosted directly (not as a
/// web app, since this test never calls its HTTP endpoints — stock is
/// seeded straight into its database instead).
///
/// The two Program classes (OrderService's and InventoryService's) would
/// collide by name if both assemblies were referenced normally, since
/// top-level statements generate an unqualified `Program` in each assembly.
/// InventoryService is referenced under the `InventoryAlias` extern alias
/// to avoid that; only its non-Program types are used here anyway.
/// </summary>
public class CheckoutSagaTests : IAsyncLifetime
{
    private const string JwtKey = "dev-only-insecure-signing-key-change-me-32bytes";
    private const string JwtIssuer = "TaskManager";
    private const string JwtAudience = "TaskManager";

    // Using the same image tags as docker-compose.yml (postgres:17, rabbitmq:3-management)
    // rather than -alpine variants: the alpine RabbitMQ tag hit an "exec format error"
    // (architecture mismatch) on this machine, while these tags are already proven to work.
    private readonly PostgreSqlContainer _orderDb = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private readonly PostgreSqlContainer _inventoryDb = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder().WithImage("rabbitmq:3-management").Build();

    private WebApplicationFactory<Program>? _orderFactory;
    private IHost? _inventoryHost;
    private HttpClient _orderClient = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_orderDb.StartAsync(), _inventoryDb.StartAsync(), _rabbitMq.StartAsync());

        // RabbitMQ's TCP port accepts connections slightly before its internal
        // auth backend has fully initialized (worse on the non-alpine image,
        // which has more to load) - an immediate connection attempt can get a
        // spurious ACCESS_REFUSED. A short grace period avoids that race.
        await Task.Delay(TimeSpan.FromSeconds(5));

        var rabbitUri = new Uri(_rabbitMq.GetConnectionString());

        // Testcontainers.RabbitMq generates its own username/password rather
        // than using "guest"/"guest" - read the real credentials back out of
        // the connection string instead of hardcoding a default that doesn't
        // match, which otherwise causes a persistent (not transient)
        // ACCESS_REFUSED no matter how long you wait for the container.
        var rabbitUserInfo = rabbitUri.UserInfo.Split(':', 2);
        var rabbitUsername = Uri.UnescapeDataString(rabbitUserInfo[0]);
        var rabbitPassword = Uri.UnescapeDataString(rabbitUserInfo[1]);

        _orderFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // UseSetting (not ConfigureAppConfiguration+AddInMemoryCollection):
            // Program.cs reads builder.Configuration.GetConnectionString(...)
            // immediately after WebApplication.CreateBuilder(args), before a
            // later-added ConfigureAppConfiguration source is guaranteed to be
            // layered in yet. UseSetting writes directly into the same
            // configuration WebApplicationFactory hands to CreateBuilder.
            builder.UseSetting("ConnectionStrings:OrderDatabase", _orderDb.GetConnectionString());
            builder.UseSetting("RabbitMq:Host", rabbitUri.Host);
            builder.UseSetting("RabbitMq:Port", rabbitUri.Port.ToString());
            builder.UseSetting("RabbitMq:Username", rabbitUsername);
            builder.UseSetting("RabbitMq:Password", rabbitPassword);
        });
        _orderClient = _orderFactory.CreateClient();

        using (var scope = _orderFactory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Database.MigrateAsync();
        }

        _inventoryHost = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<InventoryDbContext>(o => o.UseNpgsql(_inventoryDb.GetConnectionString()));
                services.AddScoped<IInventoryRepository, InventoryRepository>();

                services.AddMassTransit(x =>
                {
                    x.AddEntityFrameworkOutbox<InventoryDbContext>(o => o.UsePostgres());
                    x.AddConsumer<ReserveStockConsumer>();
                    x.AddConsumer<ReleaseStockConsumer>();

                    x.UsingRabbitMq((context, cfg) =>
                    {
                        cfg.Host(rabbitUri.Host, (ushort)rabbitUri.Port, "/", h =>
                        {
                            h.Username(rabbitUsername);
                            h.Password(rabbitPassword);
                        });

                        cfg.ReceiveEndpoint("ReserveStock", e =>
                        {
                            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
                            e.ConfigureConsumer<ReserveStockConsumer>(context);
                        });

                        cfg.ReceiveEndpoint("ReleaseStock", e =>
                        {
                            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
                            e.ConfigureConsumer<ReleaseStockConsumer>(context);
                        });
                    });
                });
            })
            .Build();

        using (var scope = _inventoryHost.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.MigrateAsync();
        }

        await _inventoryHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_inventoryHost is not null)
        {
            await _inventoryHost.StopAsync();
            _inventoryHost.Dispose();
        }

        _orderFactory?.Dispose();

        await Task.WhenAll(_orderDb.DisposeAsync().AsTask(), _inventoryDb.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Checkout_WithSufficientStock_ConfirmsOrderAndDecrementsInventory()
    {
        var productId = Guid.NewGuid();
        await SeedInventoryAsync(productId, quantityAvailable: 10);
        AuthenticateAs(Guid.NewGuid());

        var orderId = await CreateOrderAsync(new OrderItemRequest(productId, 4, 9.99m));

        var order = await WaitForResolutionAsync(orderId);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(6, await GetInventoryQuantityAsync(productId));
    }

    [Fact]
    public async Task Checkout_WithInsufficientStock_CancelsOrderAndReleasesReservedItems()
    {
        var plentifulProductId = Guid.NewGuid();
        var scarceProductId = Guid.NewGuid();
        await SeedInventoryAsync(plentifulProductId, quantityAvailable: 10);
        await SeedInventoryAsync(scarceProductId, quantityAvailable: 1);
        AuthenticateAs(Guid.NewGuid());

        var orderId = await CreateOrderAsync(
            new OrderItemRequest(plentifulProductId, 3, 9.99m),
            new OrderItemRequest(scarceProductId, 5, 4.99m));

        var order = await WaitForResolutionAsync(orderId);

        Assert.Equal(OrderStatus.Cancelled, order.Status);

        // The plentiful item's reservation must have been released once the
        // scarce item failed — this is the compensating transaction actually
        // firing, not just the order being marked cancelled.
        Assert.Equal(10, await GetInventoryQuantityAsync(plentifulProductId));
        Assert.Equal(1, await GetInventoryQuantityAsync(scarceProductId));
    }

    private async Task SeedInventoryAsync(Guid productId, int quantityAvailable)
    {
        using var scope = _inventoryHost!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        db.Add(new InventoryItem(productId, quantityAvailable));
        await db.SaveChangesAsync();
    }

    private async Task<int> GetInventoryQuantityAsync(Guid productId)
    {
        using var scope = _inventoryHost!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = await db.Set<InventoryItem>().AsNoTracking().FirstAsync(x => x.ProductId == productId);
        return item.QuantityAvailable;
    }

    private void AuthenticateAs(Guid userId)
    {
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: new[] { new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()) },
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        var rawToken = new JwtSecurityTokenHandler().WriteToken(token);
        _orderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
    }

    private async Task<Guid> CreateOrderAsync(params OrderItemRequest[] items)
    {
        var response = await _orderClient.PostAsJsonAsync("/orders", new CreateOrderRequest(items));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CreatedOrderResponse>();
        return body!.Id;
    }

    private async Task<OrderDto> WaitForResolutionAsync(Guid orderId, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var response = await _orderClient.GetAsync($"/orders/{orderId}");
            response.EnsureSuccessStatusCode();
            var order = await response.Content.ReadFromJsonAsync<OrderDto>();

            if (order!.Status != OrderStatus.Pending)
                return order;

            await Task.Delay(250);
        }

        throw new TimeoutException($"Order {orderId} did not resolve out of Pending within {timeoutSeconds}s.");
    }

    private sealed record CreatedOrderResponse(Guid Id);
}
