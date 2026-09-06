using System.Security.Claims;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OrderService;
using OrderService.Application;
using OrderService.Application.Orders;
using OrderService.Application.Sagas;
using OrderService.Domain;
using OrderService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("OrderDatabase")
    ?? throw new InvalidOperationException("OrderDatabase connection string is missing.");

// Deliberately not using EnableRetryOnFailure here: EF Core's retrying
// execution strategy doesn't support user-initiated transactions, and
// MassTransit's saga repository wraps each message in exactly that kind
// of explicit transaction (visible as "SELECT ... FOR UPDATE" + saga
// save in one transaction in the logs). Message-level retry
// (UseMessageRetry, below) already covers this class of transient
// failure at a layer that's actually compatible with the saga.
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>();

builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<OrdersService>();

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key configuration is missing.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "TaskManager";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "TaskManager";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<OrderDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.AddSagaStateMachine<OrderSagaStateMachine, OrderSagaState>()
        .EntityFrameworkRepository(r =>
        {
            r.ExistingDbContext<OrderDbContext>();
            r.UsePostgres();
        });

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitMqPort = builder.Configuration.GetValue<ushort?>("RabbitMq:Port") ?? 5672;

        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", rabbitMqPort, "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        // StockReserved and StockReservationFailed for the same order can
        // arrive nearly simultaneously (concurrent ReserveStock calls), and
        // both try to lock/update the same saga row. Postgres correctly
        // rejects one under serializable isolation (40001: could not
        // serialize access due to concurrent update) - that's an expected,
        // recoverable race, not a real failure, so retry a few times rather
        // than faulting the message.
        cfg.UseMessageRetry(r => r.Intervals(100, 250, 500, 1000));

        // Stops hammering a dependency that's genuinely down (as opposed to
        // the momentary concurrency conflicts UseMessageRetry above handles)
        // instead of retrying every message indefinitely.
        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold = 15;
            cb.ActiveThreshold = 10;
            cb.ResetInterval = TimeSpan.FromMinutes(5);
        });

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/orders", async (
    ClaimsPrincipal user,
    OrdersService service,
    CancellationToken cancellationToken) =>
{
    var orders = await service.GetAllAsync(user.GetUserId(), cancellationToken);

    return Results.Ok(orders);
}).RequireAuthorization();

app.MapGet("/orders/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    OrdersService service,
    CancellationToken cancellationToken) =>
{
    var order = await service.GetByIdAsync(id, user.GetUserId(), cancellationToken);

    return order is null ? Results.NotFound() : Results.Ok(order);
}).RequireAuthorization();

app.MapPost("/orders", async (
    CreateOrderRequest request,
    ClaimsPrincipal user,
    OrdersService service,
    CancellationToken cancellationToken) =>
{
    var id = await service.CreateAsync(request, user.GetUserId(), cancellationToken);

    return Results.Created($"/orders/{id}", new { id });
}).RequireAuthorization();

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();

public partial class Program
{
}
