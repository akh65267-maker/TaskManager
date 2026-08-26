namespace TaskService.Domain;

/// <summary>
/// A local read-model projection of a user, populated from the UserRegistered
/// integration event. Not the source of truth for user data (UserService owns
/// that) — only enough to let TaskService validate task ownership without a
/// synchronous call to UserService.
/// </summary>
public class KnownUser
{
    public Guid UserId { get; private set; }
    public string Email { get; private set; }

    private KnownUser()
    {
    }

    public KnownUser(Guid userId, string email)
    {
        UserId = userId;
        Email = email;
    }
}
