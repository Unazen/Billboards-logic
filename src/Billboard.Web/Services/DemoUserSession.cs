namespace Billboard.Web.Services;

public sealed record DemoAccount(
    string Key,
    string Name,
    string Email,
    int? UserId,
    bool IsAdministrator
);

public sealed class DemoUserSession
{
    private readonly AdminDemoStore _adminStore;

    public DemoUserSession(AdminDemoStore adminStore)
    {
        _adminStore = adminStore;
        CurrentUserId = adminStore.Users.FirstOrDefault()?.Id ?? 0;
    }

    public int? CurrentUserId { get; private set; }

    public bool IsAdministrator { get; private set; }

    public string CurrentName => IsAdministrator
        ? "Администратор"
        : _adminStore.Users.FirstOrDefault(user => user.Id == CurrentUserId)?.Name ?? "Пользователь";

    public string CurrentEmail => IsAdministrator
        ? "admin@billboard.local"
        : _adminStore.Users.FirstOrDefault(user => user.Id == CurrentUserId)?.Email ?? string.Empty;

    public IReadOnlyList<DemoAccount> Accounts =>
    [
        new("admin", "Администратор", "admin@billboard.local", null, true),
        .. _adminStore.Users.Select(user => new DemoAccount(
            $"user-{user.Id}",
            user.Name,
            user.Email,
            user.Id,
            false))
    ];

    public event Action? Changed;

    public bool TrySignIn(string accountKey)
    {
        var account = Accounts.FirstOrDefault(item =>
            string.Equals(item.Key, accountKey, StringComparison.Ordinal));

        if (account is null)
            return false;

        CurrentUserId = account.UserId;
        IsAdministrator = account.IsAdministrator;
        Changed?.Invoke();
        return true;
    }
}
