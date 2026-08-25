namespace TaskService.Domain;

public class TaskItem
{
    public Guid Id { get; set; }
    public string Title { get; private set; }
    public bool IsCompleted { get; private set; }
    private TaskItem()
    {
    }

    public TaskItem(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Task title is required.", nameof(title));

        Id = Guid.NewGuid();
        Title = title;
    }

    public void Complete()
    {
        IsCompleted = true;
    }
}
