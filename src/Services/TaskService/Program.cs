using System.Security.Claims;
using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TaskService;
using TaskService.Application;
using TaskService.Application.Consumers;
using TaskService.Application.Tasks;
using TaskService.Domain;
using TaskService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TaskDatabase")
    ?? throw new InvalidOperationException("TaskDatabase connection string is missing.");

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
    x.AddConsumer<UserRegisteredConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

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
