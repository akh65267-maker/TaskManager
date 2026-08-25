using MassTransit;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddScoped<TasksService>();

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

app.MapGet("/tasks", async (
    TasksService service,
    CancellationToken cancellationToken) =>
{
    var tasks = await service.GetAllAsync(cancellationToken);

    return Results.Ok(tasks);
});

app.MapGet("/tasks/{id:guid}", async (
    Guid id,
    TasksService service,
    CancellationToken cancellationToken) =>
{
    var task = await service.GetByIdAsync(id, cancellationToken);

    return task is null ? Results.NotFound() : Results.Ok(task);
});

app.MapPost("/tasks", async (
    CreateTaskRequest request,
    TasksService service,
    CancellationToken cancellationToken) =>
{
    var id = await service.CreateAsync(request, cancellationToken);

    return Results.Created($"/tasks/{id}", new { id });
});

app.MapPost("/tasks/{id:guid}/complete", async (
    Guid id,
    TasksService service,
    CancellationToken cancellationToken) =>
{
    var completed = await service.CompleteAsync(id, cancellationToken);

    return completed ? Results.NoContent() : Results.NotFound();
});

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
