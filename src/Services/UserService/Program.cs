using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using UserService;
using UserService.Application;
using UserService.Application.Users;
using UserService.Domain;
using UserService.Infrastructure.Persistence;
using UserService.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("UserDatabase")
    ?? throw new InvalidOperationException("UserDatabase connection string is missing.");

// Deliberately not using EnableRetryOnFailure: this DbContext backs
// MassTransit's transactional outbox (UseBusOutbox), which wraps
// SaveChanges in a way that's incompatible with EF's retrying
// execution strategy.
builder.Services.AddDbContext<UserDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<UserDbContext>();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<UsersService>();

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
    x.AddEntityFrameworkOutbox<UserDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

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

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/users", async (
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var users = await service.GetAllAsync(cancellationToken);

    return Results.Ok(users);
}).RequireAuthorization();

app.MapGet("/users/{id:guid}", async (
    Guid id,
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var user = await service.GetByIdAsync(id, cancellationToken);

    return user is null ? Results.NotFound() : Results.Ok(user);
}).RequireAuthorization();

app.MapPost("/users", async (
    RegisterUserRequest request,
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var id = await service.RegisterAsync(request, cancellationToken);

    return Results.Created($"/users/{id}", new { id });
});

app.MapPost("/users/login", async (
    LoginRequest request,
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.LoginAsync(request, cancellationToken);

    return Results.Ok(result);
});

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
