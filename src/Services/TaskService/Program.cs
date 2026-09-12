using System.Security.Claims;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Observability;
using Serilog;
using TaskService;
using TaskService.Application;
using TaskService.Application.Consumers;
using TaskService.Application.Tasks;
using TaskService.Domain;
using TaskService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("task-service", "MassTransit");

var connectionString = builder.Configuration.GetConnectionString("TaskDatabase")
    ?? throw new InvalidOperationException("TaskDatabase connection string is missing.");

// Deliberately not using EnableRetryOnFailure: this DbContext is used by
// MassTransit's EF Core inbox (UseEntityFrameworkOutbox on the
// UserRegistered receive endpoint), which wraps each message in an
// explicit transaction - incompatible with EF's retrying execution
// strategy. Message-level retry (UseMessageRetry, below) covers this
// instead, at a layer that's actually compatible.
builder.Services.AddDbContext<TaskDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<TaskDbContext>();

builder.Services.AddScoped<ITaskRepository, TaskRepository>();
builder.Services.AddScoped<IKnownUserRepository, KnownUserRepository>();
builder.Services.AddScoped<TasksService>();

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
    x.AddEntityFrameworkOutbox<TaskDbContext>(o =>
    {
        o.UsePostgres();
    });

    x.AddConsumer<UserRegisteredConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        // Explicit receive endpoint (instead of convention-based ConfigureEndpoints)
        // so we can attach the EF Core inbox: it deduplicates by MessageId+ConsumerId
        // via InboxState, skipping the consumer entirely on redelivery of the same
        // message instead of relying only on KnownUserRepository's own existence check.
        cfg.ReceiveEndpoint("UserRegistered", e =>
        {
            // Retries a handful of times with backoff for transient failures
            // (a Postgres blip, a lock timeout), then a circuit breaker stops
            // hammering a dependency that's genuinely down instead of
            // retrying every single message indefinitely.
            e.UseMessageRetry(r => r.Intervals(100, 500, 1000, 5000));
            e.UseCircuitBreaker(cb =>
            {
                cb.TrackingPeriod = TimeSpan.FromMinutes(1);
                cb.TripThreshold = 15;
                cb.ActiveThreshold = 10;
                cb.ResetInterval = TimeSpan.FromMinutes(5);
            });

            e.UseEntityFrameworkOutbox<TaskDbContext>(context);
            e.ConfigureConsumer<UserRegisteredConsumer>(context);
        });
    });
});

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSerilogRequestLogging();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/tasks", async (
    ClaimsPrincipal user,
    TasksService service,
    CancellationToken cancellationToken) =>
{
    var tasks = await service.GetAllAsync(user.GetUserId(), cancellationToken);

    return Results.Ok(tasks);
}).RequireAuthorization();

app.MapGet("/tasks/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    TasksService service,
    CancellationToken cancellationToken) =>
{
    var task = await service.GetByIdAsync(id, user.GetUserId(), cancellationToken);

    return task is null ? Results.NotFound() : Results.Ok(task);
}).RequireAuthorization();

app.MapPost("/tasks", async (
    CreateTaskRequest request,
    ClaimsPrincipal user,
    TasksService service,
    CancellationToken cancellationToken) =>
{
    var id = await service.CreateAsync(request, user.GetUserId(), cancellationToken);

    return Results.Created($"/tasks/{id}", new { id });
}).RequireAuthorization();

app.MapPost("/tasks/{id:guid}/complete", async (
    Guid id,
    ClaimsPrincipal user,
    TasksService service,
    CancellationToken cancellationToken) =>
{
    var completed = await service.CompleteAsync(id, user.GetUserId(), cancellationToken);

    return completed ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
