using Contracts.IntegrationEvents;
using MassTransit;

namespace TaskService.Application.Consumers;

public class UserRegisteredConsumer : IConsumer<UserRegistered>
{
    private readonly ILogger<UserRegisteredConsumer> _logger;

    public UserRegisteredConsumer(ILogger<UserRegisteredConsumer> logger)
    {
        _logger = logger;
    }

    public Task Consume(ConsumeContext<UserRegistered> context)
    {
        _logger.LogInformation(
            "TaskService observed UserRegistered for {UserId} ({Email})",
            context.Message.UserId,
            context.Message.Email);

        return Task.CompletedTask;
    }
}
