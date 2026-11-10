using Billboard.Application.Contracts;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace Billboard.Web.Services;

public sealed class ApiSession
{
    public string? AccessToken { get; private set; }

    public AccountDto? Account { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken) && Account is not null;

    public bool IsAdministrator => Account?.Roles.Contains("Admin", StringComparer.OrdinalIgnoreCase) == true;

    public string CurrentName => Account?.DisplayName ?? string.Empty;

    public string CurrentEmail => Account?.Email ?? string.Empty;

    public event Action? Changed;

    public void SetAuthenticated(string accessToken, AccountDto account)
    {
        AccessToken = accessToken;
        Account = account;
        Changed?.Invoke();
    }

    public void Clear()
    {
        AccessToken = null;
        Account = null;
        Changed?.Invoke();
    }
}

public sealed class BrowserAuthStorage(ProtectedSessionStorage storage)
{
    private const string TokenKey = "billboard.access-token";

    public async Task<string?> GetTokenAsync()
    {
        var result = await storage.GetAsync<string>(TokenKey);
        return result.Success ? result.Value : null;
    }

    public ValueTask SaveTokenAsync(string token) =>
        storage.SetAsync(TokenKey, token);

    public ValueTask ClearTokenAsync() =>
        storage.DeleteAsync(TokenKey);
}
