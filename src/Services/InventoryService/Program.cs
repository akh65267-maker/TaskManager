using System.Text;
using InventoryService;
using InventoryService.Application;
using InventoryService.Application.Inventory;
using InventoryService.Domain;
using InventoryService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("InventoryDatabase")
    ?? throw new InvalidOperationException("InventoryDatabase connection string is missing.");

builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<InventoryDbContext>();

builder.Services.AddScoped<IInventoryRepository, InventoryRepository>();
builder.Services.AddScoped<InventoryItemsService>();

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

app.MapGet("/inventory", async (
    InventoryItemsService service,
    CancellationToken cancellationToken) =>
{
    var items = await service.GetAllAsync(cancellationToken);

    return Results.Ok(items);
}).RequireAuthorization();

app.MapGet("/inventory/{productId:guid}", async (
    Guid productId,
    InventoryItemsService service,
    CancellationToken cancellationToken) =>
{
    var item = await service.GetByProductIdAsync(productId, cancellationToken);

    return item is null ? Results.NotFound() : Results.Ok(item);
}).RequireAuthorization();

app.MapPost("/inventory", async (
    CreateInventoryItemRequest request,
    InventoryItemsService service,
    CancellationToken cancellationToken) =>
{
    await service.CreateAsync(request, cancellationToken);

    return Results.Created($"/inventory/{request.ProductId}", new { productId = request.ProductId });
}).RequireAuthorization();

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
