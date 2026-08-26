namespace TaskService.Application.Tasks;

public sealed record TaskDto(Guid Id, string Title, bool IsCompleted, Guid OwnerId);
