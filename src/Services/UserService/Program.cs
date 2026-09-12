using System.Text;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Observability;
using Serilog;
using UserService;
using UserService.Application;
using UserService.Application.Users;
using UserService.Domain;
using UserService.Infrastructure.Persistence;
using UserService.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("user-service", "MassTransit");

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

app.UseSerilogRequestLogging();

// Seeds one admin account from configuration if it doesn't exist yet.
// There's no invite/promote-user flow, so this is the only way an Admin
// account comes to exist. The default password below is a dev-only
// placeholder - production deployments must override Admin:Password via
// a real secret, not commit a real password to appsettings.json.
using (var scope = app.Services.CreateScope())
{
    var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

    var adminEmail = app.Configuration["Admin:Email"];
    var adminPassword = app.Configuration["Admin:Password"];
    var adminDisplayName = app.Configuration["Admin:DisplayName"] ?? "Admin";

    if (!string.IsNullOrWhiteSpace(adminEmail)
        && !string.IsNullOrWhiteSpace(adminPassword)
        && !await userRepo.EmailExistsAsync(adminEmail))
    {
        var admin = new User(adminEmail, adminDisplayName, passwordHasher.Hash(adminPassword), UserRole.Admin);
        await userRepo.AddAsync(admin);
        await userRepo.SaveChangesAsync();
    }
}

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
