namespace UserService.Domain;

public class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; }
    public string DisplayName { get; private set; }

    private User()
    {
    }

    public User(string email, string displayName)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));

        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        Id = Guid.NewGuid();
        Email = email;
        DisplayName = displayName;
    }
}
