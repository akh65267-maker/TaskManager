using Microsoft.Extensions.Logging;
using Moq;
using TaskService.Application;
using TaskService.Application.Tasks;
using TaskService.Domain;

namespace TaskService.UnitTests.Application;

public class TasksServiceTests
{
    private readonly Mock<ITaskRepository> _taskRepo = new();
    private readonly Mock<IKnownUserRepository> _knownUsers = new();
    private readonly TasksService _sut;

    public TasksServiceTests()
    {
        _sut = new TasksService(_taskRepo.Object, _knownUsers.Object, Mock.Of<ILogger<TasksService>>());
    }

    [Fact]
    public async Task CreateAsync_WithUnknownOwner_ThrowsWithoutCreatingTask()
    {
        var ownerId = Guid.NewGuid();
        _knownUsers.Setup(x => x.ExistsAsync(ownerId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateAsync(new CreateTaskRequest("Buy milk", ownerId)));

        _taskRepo.Verify(x => x.AddAsync(It.IsAny<TaskItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WithKnownOwner_AddsTaskAndReturnsId()
    {
        var ownerId = Guid.NewGuid();
        _knownUsers.Setup(x => x.ExistsAsync(ownerId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var id = await _sut.CreateAsync(new CreateTaskRequest("Buy milk", ownerId));

        Assert.NotEqual(Guid.Empty, id);
        _taskRepo.Verify(x => x.AddAsync(
            It.Is<TaskItem>(t => t.Title == "Buy milk" && t.OwnerId == ownerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidTitle_ThrowsWithoutCheckingOwner()
    {
        var ownerId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateAsync(new CreateTaskRequest("", ownerId)));

        _knownUsers.Verify(x => x.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteAsync_UnknownId_ReturnsFalse()
    {
        _taskRepo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaskItem?)null);

        var result = await _sut.CompleteAsync(Guid.NewGuid());

        Assert.False(result);
        _taskRepo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteAsync_KnownId_CompletesAndSaves()
    {
        var task = new TaskItem("Buy milk", Guid.NewGuid());
        _taskRepo.Setup(x => x.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        var result = await _sut.CompleteAsync(task.Id);

        Assert.True(result);
        Assert.True(task.IsCompleted);
        _taskRepo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _taskRepo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaskItem?)null);

        var result = await _sut.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }
}
