using MassTransit;
using Microsoft.EntityFrameworkCore;
using UserService;
using UserService.Application;
using UserService.Application.Users;
using UserService.Domain;
using UserService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("UserDatabase")
    ?? throw new InvalidOperationException("UserDatabase connection string is missing.");

builder.Services.AddDbContext<UserDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<UserDbContext>();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<UsersService>();

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });
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

app.MapGet("/users", async (
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var users = await service.GetAllAsync(cancellationToken);

    return Results.Ok(users);
});

app.MapGet("/users/{id:guid}", async (
    Guid id,
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var user = await service.GetByIdAsync(id, cancellationToken);

    return user is null ? Results.NotFound() : Results.Ok(user);
});

app.MapPost("/users", async (
    RegisterUserRequest request,
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var id = await service.RegisterAsync(request, cancellationToken);

    return Results.Created($"/users/{id}", new { id });
});

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
