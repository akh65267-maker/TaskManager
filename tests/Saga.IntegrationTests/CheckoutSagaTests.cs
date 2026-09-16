extern alias InventoryAlias;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Contracts.Commands;
using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

        var rabbitUri = new Uri(_rabbitMq.GetConnectionString());

        // Testcontainers.RabbitMq generates its own username/password rather
        // than using "guest"/"guest" - read the real credentials back out of
        // the connection string instead of hardcoding a default that doesn't
        // match, which otherwise causes a persistent (not transient)
        // ACCESS_REFUSED no matter how long you wait for the container.
        var rabbitUserInfo = rabbitUri.UserInfo.Split(':', 2);
        var rabbitUsername = Uri.UnescapeDataString(rabbitUserInfo[0]);
        var rabbitPassword = Uri.UnescapeDataString(rabbitUserInfo[1]);

        // RabbitMQ's TCP port (5672) becomes reachable before its auth backend
        // and exchange infrastructure are fully initialized. On cold CI runners
        // with the non-alpine image this takes noticeably longer than locally;
        // 15 s is enough headroom without relying on the management port (15672),
        // which RabbitMqBuilder does not map by default.
        await Task.Delay(TimeSpan.FromSeconds(15));

        // Migrate via a standalone DbContext, built directly from a connection
        // string rather than resolved from _orderFactory.Services. Touching
        // WebApplicationFactory's service provider at all (including just for
        // a migration scope) starts the whole ASP.NET host and its background
        // services - the MassTransit bus and its outbox-delivery poller start
        // immediately and can hit "relation OutboxState does not exist" before
        // migrations (which would otherwise run right after) ever get a
        // chance to create it. This was a real, reproducible bug, not just a
        // slow environment: one hosted service's outbox poll would fail on
        // its very first tick and never successfully deliver that order's
        // OrderSubmitted event afterward.
        var orderDbOptions = new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(_orderDb.GetConnectionString())
            .Options;
        await using (var migrationContext = new OrderDbContext(orderDbOptions))
        {
            await migrationContext.Database.MigrateAsync();
        }

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

            builder.ConfigureServices(WaitForBusTopology);
        });
        _orderClient = _orderFactory.CreateClient();

        _inventoryHost = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<InventoryDbContext>(o => o.UseNpgsql(_inventoryDb.GetConnectionString()));
                services.AddScoped<IInventoryRepository, InventoryRepository>();

                WaitForBusTopology(services);

                services.AddMassTransit(x =>
                {
                    x.AddEntityFrameworkOutbox<InventoryDbContext>(o => o.UsePostgres());
                    x.AddConsumer<ReserveStockConsumer>();
                    x.AddConsumer<ReleaseStockConsumer>();
                    x.AddConsumer<FaultRecorder>();

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

                        // Test-only. When a message exhausts UseMessageRetry,
                        // MassTransit parks it in an _error queue and publishes
                        // a Fault<T>. Nothing in this system consumes either
                        // (see docs/TODO.md), so such a failure is completely
                        // silent and the saga simply never progresses - which
                        // surfaces only as a timeout with no cause. Binding the
                        // fault exchanges here turns that into a readable error.
                        cfg.ReceiveEndpoint("saga-test-fault-recorder", e =>
                        {
                            e.ConfigureConsumer<FaultRecorder>(context);
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

    /// <summary>
    /// Blocks host startup until MassTransit has finished declaring its
    /// exchanges, queues and bindings.
    ///
    /// MassTransitHostOptions.WaitUntilStarted defaults to false, so
    /// StartAsync returns as soon as the bus has been *asked* to start. The
    /// topology is still being declared in the background. A message
    /// published in that window goes to an exchange that has no queue bound
    /// to it yet, and RabbitMQ silently discards it: no consumer runs, no
    /// fault is produced, and the outbox considers it delivered. The saga
    /// then never starts and the order sits in Pending until the test times
    /// out with nothing to show for it.
    ///
    /// A fast machine usually wins that race, which is why this only ever
    /// failed on cold CI runners. Waiting removes the race instead of
    /// widening the window it has to win in.
    /// </summary>
    private static void WaitForBusTopology(IServiceCollection services) =>
        services.AddOptions<MassTransitHostOptions>().Configure(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(60);
        });

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

        // Sampled immediately, because the count at timeout cannot distinguish
        // "written and since delivered" from "never written at all" - both read
        // as zero 90 s later. This is the sample that tells them apart.
        _outboxAfterCreate = await CountOutboxRowsAsync();

        return body!.Id;
    }

    /// <summary>
    /// MassTransit registers a bus health check whose description names each
    /// configured receive endpoint and its state. The CI log only ever shows
    /// the tail of the run, so the startup lines that would reveal a missing
    /// or failed saga endpoint are never visible - this reads the same
    /// information back at the point of failure.
    /// </summary>
    private async Task<string> DescribeBusHealthAsync()
    {
        try
        {
            var health = _orderFactory!.Services.GetRequiredService<HealthCheckService>();
            var report = await health.CheckHealthAsync();

            return string.Join(", ", report.Entries.Select(e =>
                $"{e.Key}={e.Value.Status}{(string.IsNullOrEmpty(e.Value.Description) ? "" : $" ({e.Value.Description})")}"));
        }
        catch (Exception ex)
        {
            return $"(health check failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Reads the broker's real topology via rabbitmqctl inside the container.
    /// The outbox proves the message reached RabbitMQ and the bus reports
    /// Healthy, yet nothing consumed it and nothing faulted - which is what a
    /// publish to an exchange with no queue bound to it looks like. That has
    /// been inferred twice now without being checked; list_queues shows
    /// whether the saga's queue exists and has a consumer, and list_bindings
    /// shows whether the OrderSubmitted exchange actually routes to it.
    /// Uses exec rather than the management HTTP API, which RabbitMqBuilder
    /// does not expose.
    /// </summary>
    private async Task<string> DescribeBrokerTopologyAsync()
    {
        try
        {
            var queues = await _rabbitMq.ExecAsync(new[]
            {
                "rabbitmqctl", "list_queues", "name", "messages", "consumers", "--no-table-headers"
            });
            var bindings = await _rabbitMq.ExecAsync(new[]
            {
                "rabbitmqctl", "list_bindings", "source_name", "destination_name", "--no-table-headers"
            });

            return $"queues:{Environment.NewLine}{Truncate(queues.Stdout)}{Environment.NewLine}" +
                   $"bindings:{Environment.NewLine}{Truncate(bindings.Stdout)}";
        }
        catch (Exception ex)
        {
            return $"(broker topology query failed: {ex.Message})";
        }

        static string Truncate(string value) =>
            string.IsNullOrWhiteSpace(value) ? "(empty)"
            : value.Length <= 4000 ? value.TrimEnd()
            : value[..4000] + " …(truncated)";
    }

    private string _outboxAfterCreate = "(not sampled)";

    private async Task<string> CountOutboxRowsAsync()
    {
        try
        {
            var options = new DbContextOptionsBuilder<OrderDbContext>()
                .UseNpgsql(_orderDb.GetConnectionString())
                .Options;
            await using var db = new OrderDbContext(options);

            var messages = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"OutboxMessage\"")
                .SingleAsync();
            var states = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"OutboxState\"")
                .SingleAsync();

            return $"OutboxMessage={messages}, OutboxState={states}";
        }
        catch (Exception ex)
        {
            return $"(query failed: {ex.Message})";
        }
    }

    // The service serializes OrderStatus as a string via JsonStringEnumConverter
    // (configured in OrderService/Program.cs). ReadFromJsonAsync uses default
    // options with no converter, so we must provide one here or the deserializer
    // throws when it sees "Pending"/"Confirmed"/"Cancelled" instead of an integer.
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<OrderDto> WaitForResolutionAsync(Guid orderId, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var response = await _orderClient.GetAsync($"/orders/{orderId}");
            response.EnsureSuccessStatusCode();
            var order = await response.Content.ReadFromJsonAsync<OrderDto>(_jsonOptions);

            if (order!.Status != OrderStatus.Pending)
                return order;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Order {orderId} did not resolve out of Pending within {timeoutSeconds}s. " +
            await DescribeStalledStateAsync());
    }

    /// <summary>
    /// Explains *where* a stalled checkout stopped, so a CI timeout is
    /// actionable. Distinguishes the three possible stalls: the OrderSubmitted
    /// event never left the outbox, the saga never started, or the saga started
    /// but is still waiting on stock responses.
    /// </summary>
    private async Task<string> DescribeStalledStateAsync()
    {
        var faults = _faults.IsEmpty
            ? "none"
            : string.Join(" | ", _faults);

        try
        {
            var options = new DbContextOptionsBuilder<OrderDbContext>()
                .UseNpgsql(_orderDb.GetConnectionString())
                .Options;
            await using var db = new OrderDbContext(options);

            var sagaStates = await db.Database
                .SqlQueryRaw<string>("SELECT coalesce(string_agg(\"CurrentState\", ','), '(no rows)') AS \"Value\" FROM \"OrderSagaStates\"")
                .SingleAsync();

            return $"outbox right after create: [{_outboxAfterCreate}]; " +
                   $"outbox now: [{await CountOutboxRowsAsync()}]; " +
                   $"saga states: {sagaStates}; faults: {faults}; " +
                   $"order bus health: [{await DescribeBusHealthAsync()}]{Environment.NewLine}" +
                   await DescribeBrokerTopologyAsync();
        }
        catch (Exception ex)
        {
            return $"faults: {faults}; (diagnostics query failed: {ex.Message})";
        }
    }

    // Static so the recorder consumer (constructed by DI in the inventory host)
    // can report back into the test that is currently running.
    private static readonly ConcurrentBag<string> _faults = new();

    private sealed class FaultRecorder :
        IConsumer<Fault<OrderSubmitted>>,
        IConsumer<Fault<ReserveStock>>,
        IConsumer<Fault<ReleaseStock>>,
        IConsumer<Fault<StockReserved>>,
        IConsumer<Fault<StockReservationFailed>>
    {
        public Task Consume(ConsumeContext<Fault<OrderSubmitted>> context) => Record(context.Message);
        public Task Consume(ConsumeContext<Fault<ReserveStock>> context) => Record(context.Message);
        public Task Consume(ConsumeContext<Fault<ReleaseStock>> context) => Record(context.Message);
        public Task Consume(ConsumeContext<Fault<StockReserved>> context) => Record(context.Message);
        public Task Consume(ConsumeContext<Fault<StockReservationFailed>> context) => Record(context.Message);

        private static Task Record<T>(Fault<T> fault) where T : class
        {
            var detail = fault.Exceptions.FirstOrDefault();
            _faults.Add($"{typeof(T).Name} faulted: {detail?.ExceptionType}: {detail?.Message}");
            return Task.CompletedTask;
        }
    }

    private sealed record CreatedOrderResponse(Guid Id);
}
