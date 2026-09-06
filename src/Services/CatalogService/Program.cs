using System.Text;
using CatalogService;
using CatalogService.Application.Products;
using CatalogService.Domain;
using CatalogService.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("CatalogDatabase")
    ?? throw new InvalidOperationException("CatalogDatabase connection string is missing.");

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null)));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CatalogDbContext>();

builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

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

app.MapGet("/products", async (
    HttpRequest httpRequest,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var query = httpRequest.Query;

    var sort = query["sort"].ToString() switch
    {
        "price-asc" => ProductSortBy.PriceAscending,
        "price-desc" => ProductSortBy.PriceDescending,
        "name" => ProductSortBy.NameAscending,
        _ => ProductSortBy.Newest,
    };

    var page = int.TryParse(query["page"], out var p) && p > 0 ? p : 1;
    var pageSize = int.TryParse(query["pageSize"], out var ps) && ps is > 0 and <= 100 ? ps : 20;

    var result = await mediator.Send(new ProductListQuery(
        Category: query["category"].ToString() is { Length: > 0 } category ? category : null,
        MinPrice: decimal.TryParse(query["minPrice"], out var minPrice) ? minPrice : null,
        MaxPrice: decimal.TryParse(query["maxPrice"], out var maxPrice) ? maxPrice : null,
        Search: query["search"].ToString() is { Length: > 0 } search ? search : null,
        Sort: sort,
        Page: page,
        PageSize: pageSize), cancellationToken);

    return Results.Ok(result);
});

app.MapGet("/products/{id:guid}", async (
    Guid id,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var product = await mediator.Send(new GetProductByIdQuery(id), cancellationToken);

    return product is null ? Results.NotFound() : Results.Ok(product);
});

app.MapPost("/products", async (
    CreateProductRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var id = await mediator.Send(new CreateProductCommand(request.Name, request.Description, request.Price, request.Category), cancellationToken);

    return Results.Created($"/products/{id}", new { id });
}).RequireAuthorization();

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
