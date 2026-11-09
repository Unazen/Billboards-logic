using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Billboard.IntegrationTests;

/// <summary>
/// Сквозные проверки HTTP API участника 3: регистрация, вход, билборды, видео,
/// кошелёк, размещение, публикация с резервом, манифест устройства,
/// идемпотентные показы и возврат резерва.
/// </summary>
public class CampaignFlowTests
{
    [Fact]
    public async Task Full_campaign_flow()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "client1@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);

        var (billboardId, deviceToken) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, clientToken);

        await TopUpAsync(client, clientToken, 20m, "topup-1");

        var placementId = await CreatePlacementAsync(client, clientToken, videoId, billboardId);

        var draft = await GetJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}");
        Assert.Equal("Draft", draft.GetProperty("status").GetString());

        var published = await PostJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var publishedBody = await published.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Scheduled", publishedBody.GetProperty("status").GetString());
        Assert.Equal(7.2m, publishedBody.GetProperty("reservedAmount").GetDecimal());

        var wallet = await GetJsonAsync(client, clientToken, "/api/v1/billing/wallet");
        Assert.Equal(12.8m, wallet.GetProperty("balance").GetDecimal());
        Assert.Equal(7.2m, wallet.GetProperty("reservedBalance").GetDecimal());

        // Устройство получает манифест с временной ссылкой на файл.
        using var deviceRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/devices/{billboardId}/manifest");
        deviceRequest.Headers.Add("X-Device-Token", deviceToken);
        using var deviceResponse = await client.SendAsync(deviceRequest);
        Assert.Equal(HttpStatusCode.OK, deviceResponse.StatusCode);
        var manifest = await deviceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var file = Assert.Single(manifest.GetProperty("files").EnumerateArray());
        Assert.Equal(videoId, file.GetProperty("videoAssetId").GetGuid());
        Assert.Equal(2048, file.GetProperty("sizeBytes").GetInt64());

        // Показ: списание ровно один раз.
        var events = new
        {
            events = new[]
            {
                new
                {
                    idempotencyKey = "evt-1",
                    placementId,
                    videoAssetId = videoId,
                    startedAt = DateTimeOffset.UtcNow,
                    durationSeconds = 15,
                    result = "Completed"
                }
            }
        };

        var first = await PostDeviceJsonAsync(client, billboardId, deviceToken, "/playback-events", events);
        Assert.Equal(1, first.GetProperty("accepted").GetInt32());

        var second = await PostDeviceJsonAsync(client, billboardId, deviceToken, "/playback-events", events);
        Assert.Equal(1, second.GetProperty("duplicates").GetInt32());

        wallet = await GetJsonAsync(client, clientToken, "/api/v1/billing/wallet");
        Assert.Equal(7.15m, wallet.GetProperty("reservedBalance").GetDecimal());

        var earnings = await GetJsonAsync(client, adminToken, "/api/v1/billing/earnings");
        Assert.Equal(0.05m, earnings.GetProperty("total").GetDecimal());

        // Отмена возвращает неиспользованный резерв.
        var cancelled = await PostJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        wallet = await GetJsonAsync(client, clientToken, "/api/v1/billing/wallet");
        Assert.Equal(19.95m, wallet.GetProperty("balance").GetDecimal());
        Assert.Equal(0m, wallet.GetProperty("reservedBalance").GetDecimal());
    }

    [Fact]
    public async Task Video_cannot_be_deleted_while_placement_is_active()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "client2@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);

        var (billboardId, _) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, clientToken);
        await TopUpAsync(client, clientToken, 20m, "topup-2");
        var placementId = await CreatePlacementAsync(client, clientToken, videoId, billboardId);
        await PostJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}/publish", null);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/videos/{videoId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clientToken);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VIDEO_IN_USE", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Foreign_placement_is_hidden_from_other_client()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var ownerToken = await RegisterAndLoginAsync(client, "owner@test.local");
        var strangerToken = await RegisterAndLoginAsync(client, "stranger@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);

        var (billboardId, _) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, ownerToken);
        var placementId = await CreatePlacementAsync(client, ownerToken, videoId, billboardId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/placements/{placementId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", strangerToken);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Publish_without_money_returns_conflict_and_awaits_payment()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "poor@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);

        var (billboardId, _) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, clientToken);
        var placementId = await CreatePlacementAsync(client, clientToken, videoId, billboardId);

        // Баланс не пополняли: резервировать нечего.
        var response = await PostJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}/publish", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INSUFFICIENT_FUNDS", body.GetProperty("code").GetString());

        var placement = await GetJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}");
        Assert.Equal("PendingPayment", placement.GetProperty("status").GetString());
    }

    // ---------- вспомогательные ----------

    private static async Task<string> RegisterAndLoginAsync(HttpClient client, string email)
    {
        var register = await client.PostAsJsonAsync("/api/v1/account/register",
            new { email, password = "Client_123", displayName = "Клиент" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        return await LoginAsync(client, email, "Client_123");
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/account/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()
               ?? throw new InvalidOperationException("Сервер не вернул accessToken.");
    }

    private static async Task<(Guid BillboardId, string DeviceToken)> CreateBillboardAsync(HttpClient client, string adminToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/billboards")
        {
            Content = JsonContent.Create(new
            {
                name = "Билборд у вокзала",
                address = "г. Минск, ул. Тестовая, 1",
                capacityBytes = 34359738368L,
                advertCapacitySecondsPerHour = 1800,
                pricePerPlay = 0.05m,
                timeZoneId = "UTC"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("billboard").GetProperty("id").GetGuid(),
                body.GetProperty("deviceToken").GetString()!);
    }

    private static async Task<Guid> UploadVideoAsync(HttpClient client, string token)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/videos/uploads")
        {
            Content = JsonContent.Create(new { fileName = "ad.mp4", contentType = "video/mp4", sizeBytes = 2048 })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var init = await created.Content.ReadFromJsonAsync<JsonElement>();
        var videoId = init.GetProperty("videoId").GetGuid();

        // Тело запроса — «файл» как есть, без multipart и JSON.
        using var upload = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/videos/{videoId}/content")
        {
            Content = new ByteArrayContent(new byte[2048])
        };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        var video = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ready", video.GetProperty("status").GetString());

        return videoId;
    }

    private static async Task TopUpAsync(HttpClient client, string token, decimal amount, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/top-ups")
        {
            Content = JsonContent.Create(new { amount, idempotencyKey = key })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Guid> CreatePlacementAsync(HttpClient client, string token, Guid videoId, Guid billboardId)
    {
        var start = DateTimeOffset.UtcNow.Date;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/placements")
        {
            Content = JsonContent.Create(new
            {
                videoAssetId = videoId,
                billboardId,
                startAt = new DateTimeOffset(start, TimeSpan.Zero),
                endAt = new DateTimeOffset(start.AddDays(1), TimeSpan.Zero),
                daysOfWeek = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" },
                startTimeOfDay = "00:00:00",
                endTimeOfDay = "23:59:00",
                minimumPlaysPerHour = 6
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client, string token, string url, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> PostDeviceJsonAsync(
        HttpClient client, Guid billboardId, string deviceToken, string suffix, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/devices/{billboardId}/{suffix.TrimStart('/')}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Device-Token", deviceToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
