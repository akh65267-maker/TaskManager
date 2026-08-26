namespace TaskService.Application.Tasks;

public sealed record CreateTaskRequest(string Title, Guid OwnerId);
