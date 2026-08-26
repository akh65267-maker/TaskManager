using TaskService.Domain;

namespace TaskService.UnitTests.Domain;

public class TaskItemTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidTitle_Throws(string? title)
    {
        Assert.Throws<ArgumentException>(() => new TaskItem(title!, OwnerId));
    }

    [Fact]
    public void Constructor_WithEmptyOwnerId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TaskItem("Buy milk", Guid.Empty));
    }

    [Fact]
    public void Constructor_WithValidArguments_SetsProperties()
    {
        var task = new TaskItem("Buy milk", OwnerId);

        Assert.NotEqual(Guid.Empty, task.Id);
        Assert.Equal("Buy milk", task.Title);
        Assert.Equal(OwnerId, task.OwnerId);
        Assert.False(task.IsCompleted);
    }

    [Fact]
    public void Complete_SetsIsCompletedTrue()
    {
        var task = new TaskItem("Buy milk", OwnerId);

        task.Complete();

        Assert.True(task.IsCompleted);
    }
}
