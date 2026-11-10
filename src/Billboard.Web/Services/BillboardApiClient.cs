using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Billboard.Application.Contracts;

namespace Billboard.Web.Services;

public sealed class BillboardApiClient(HttpClient http, ApiSession session)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AccountDto> SignInAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        session.Clear();
        using var response = await SendAsync(HttpMethod.Post, "api/v1/account/login", request, null, cancellationToken);
        var token = await response.Content.ReadFromJsonAsync<BearerTokenResponse>(JsonOptions, cancellationToken);
        if (string.IsNullOrWhiteSpace(token?.AccessToken))
            throw new BillboardApiException(HttpStatusCode.BadGateway, "INVALID_AUTH_RESPONSE", "API не вернул токен доступа.");

        using var accountResponse = await SendAsync(
            HttpMethod.Get,
            "api/v1/account/me",
            null,
            token.AccessToken,
            cancellationToken);
        var account = await ReadJsonAsync<AccountDto>(accountResponse, cancellationToken);
        session.SetAuthenticated(token.AccessToken, account);
        return account;
    }

    public async Task<AccountDto> RestoreSessionAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/account/me", null, accessToken, cancellationToken);
        var account = await ReadJsonAsync<AccountDto>(response, cancellationToken);
        session.SetAuthenticated(accessToken, account);
        return account;
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        return await ReadJsonAsync<T>(response, cancellationToken);
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "health", null, null, cancellationToken);
    }

    public async Task<(byte[] Content, string ContentType)> GetFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (content, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
    }

    public async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken cancellationToken = default) =>
        await GetAsync<List<T>>(path, cancellationToken);

    public async Task<TResponse> PostAsync<TResponse>(
        string path,
        object request,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, request, cancellationToken);
        return await ReadJsonAsync<TResponse>(response, cancellationToken);
    }

    public async Task PostAsync(string path, object request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, request, cancellationToken);
    }

    public async Task<TResponse> PatchAsync<TResponse>(
        string path,
        object request,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, path, request, cancellationToken);
        return await ReadJsonAsync<TResponse>(response, cancellationToken);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, null, cancellationToken);
    }

    public async Task<VideoDto> UploadVideoAsync(
        string fileName,
        string contentType,
        long size,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var upload = await PostAsync<UploadInitDto>(
            "api/v1/videos/uploads",
            new CreateUploadRequest(fileName, contentType, size),
            cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/videos/{upload.VideoId}/content")
        {
            Content = new StreamContent(content)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        AddAuthorization(request, session.AccessToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<VideoDto>(response, cancellationToken);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken) =>
        SendAsync(method, path, body, session.AccessToken, cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        AddAuthorization(request, accessToken);

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return response;
    }

    private static void AddAuthorization(HttpRequestMessage request, string? accessToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return value ?? throw new BillboardApiException(
            HttpStatusCode.BadGateway,
            "EMPTY_API_RESPONSE",
            "API вернул пустой ответ.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var (code, message) = ParseError(responseBody, response.ReasonPhrase);
        throw new BillboardApiException(response.StatusCode, code, message);
    }

    private static (string Code, string Message) ParseError(string responseBody, string? fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeValue)
                ? codeValue.GetString() ?? "API_ERROR"
                : "API_ERROR";
            var message = root.TryGetProperty("message", out var messageValue)
                ? messageValue.GetString() ?? "Запрос к API завершился ошибкой."
                : "Запрос к API завершился ошибкой.";
            return (code, message);
        }
        catch (JsonException)
        {
            return ("API_ERROR", string.IsNullOrWhiteSpace(responseBody) ? fallback ?? "Ошибка API." : responseBody);
        }
    }

    private sealed record BearerTokenResponse(string AccessToken, string TokenType, int ExpiresIn, string? RefreshToken);
}

public sealed class BillboardApiException(HttpStatusCode statusCode, string code, string message)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string Code { get; } = code;
}
