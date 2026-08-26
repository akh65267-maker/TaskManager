using Contracts.IntegrationEvents;
using MassTransit;
using TaskService.Domain;

namespace TaskService.Application.Consumers;

public class UserRegisteredConsumer : IConsumer<UserRegistered>
{
    private readonly IKnownUserRepository _knownUsers;
    private readonly ILogger<UserRegisteredConsumer> _logger;

    public UserRegisteredConsumer(IKnownUserRepository knownUsers, ILogger<UserRegisteredConsumer> logger)
    {
        _knownUsers = knownUsers;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<UserRegistered> context)
    {
        await _knownUsers.UpsertAsync(context.Message.UserId, context.Message.Email, context.CancellationToken);

        _logger.LogInformation(
            "TaskService recorded known user {UserId} ({Email})",
            context.Message.UserId,
            context.Message.Email);
    }
}
