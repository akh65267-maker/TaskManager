namespace TaskService.Domain;

public class TaskItem
{
    public Guid Id { get; set; }
    public string Title { get; private set; }
    public bool IsCompleted { get; private set; }
    public Guid OwnerId { get; private set; }
    private TaskItem()
    {
    }

    public TaskItem(string title, Guid ownerId)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Task title is required.", nameof(title));

        if (ownerId == Guid.Empty)
            throw new ArgumentException("Owner id is required.", nameof(ownerId));

        Id = Guid.NewGuid();
        Title = title;
        OwnerId = ownerId;
    }

    public void Complete()
    {
        IsCompleted = true;
    }
}
