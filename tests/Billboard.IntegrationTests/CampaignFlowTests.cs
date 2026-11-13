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
        Assert.Equal(placementId, file.GetProperty("placementId").GetGuid());
        Assert.Equal(6, file.GetProperty("minimumPlaysPerHour").GetInt32());
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

    [Fact]
    public async Task Admin_manages_billboard_groups()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "groups@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);
        var (billboardId, _) = await CreateBillboardAsync(client, adminToken);

        var created = await PostJsonAsync(client, adminToken, "/api/v1/admin/billboard-groups",
            new { name = "Центр" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var group = await created.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = group.GetProperty("id").GetGuid();

        var withMembers = await PostJsonAsync(client, adminToken,
            $"/api/v1/admin/billboard-groups/{groupId}/billboards",
            new { billboardIds = new[] { billboardId } });
        Assert.Equal(HttpStatusCode.OK, withMembers.StatusCode);
        var groupWithMembers = await withMembers.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(groupWithMembers.GetProperty("billboards").EnumerateArray());

        // Клиент видит группы, но управлять ими не может.
        var list = await GetJsonAsync(client, clientToken, "/api/v1/billboard-groups");
        Assert.Single(list.EnumerateArray());

        var forbidden = await PostJsonAsync(client, clientToken, "/api/v1/admin/billboard-groups",
            new { name = "Чужая группа" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Group_assignment_validates_all_billboards_and_creates_drafts()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "group-flow@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);

        var (firstBillboard, _) = await CreateBillboardAsync(client, adminToken);
        var (secondBillboard, _) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, clientToken);
        await TopUpAsync(client, clientToken, 50m, "group-topup");

        var body = new
        {
            billboardIds = new[] { firstBillboard, secondBillboard },
            template = new
            {
                videoAssetId = videoId,
                startDate = "2026-11-10",
                endDate = "2026-11-14",
                daysOfWeek = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" },
                startTime = "09:00:00",
                endTime = "18:00:00",
                minimumPlaysPerHour = 6,
                videoDurationSeconds = 15
            }
        };

        var validationResponse = await PostJsonAsync(client, clientToken, "/api/v1/placement-groups/validate", body);
        Assert.Equal(HttpStatusCode.OK, validationResponse.StatusCode);
        var validation = await validationResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(validation.GetProperty("canPublish").GetBoolean());
        Assert.Equal(2, validation.GetProperty("results").GetArrayLength());

        var draftsResponse = await PostJsonAsync(client, clientToken, "/api/v1/placement-groups/drafts", body);
        Assert.Equal(HttpStatusCode.OK, draftsResponse.StatusCode);
        var drafts = await draftsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(drafts.GetProperty("success").GetBoolean());
        Assert.Equal(2, drafts.GetProperty("placements").GetArrayLength());

        var wallet = await GetJsonAsync(client, clientToken, "/api/v1/billing/wallet");
        Assert.Equal(21.6m, wallet.GetProperty("reservedBalance").GetDecimal());
    }

    [Fact]
    public async Task Mine_filter_shows_only_rented_billboards_and_groups()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "mine@test.local");
        var otherToken = await RegisterAndLoginAsync(client, "other@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);

        var (billboardId, _) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, clientToken);
        await TopUpAsync(client, clientToken, 20m, "mine-topup");
        var placementId = await CreatePlacementAsync(client, clientToken, videoId, billboardId);
        await PostJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}/publish", null);

        var mine = await GetJsonAsync(client, clientToken, "/api/v1/billboards?mine=true");
        Assert.Contains(mine.EnumerateArray(), b => b.GetProperty("id").GetGuid() == billboardId);
        var otherMine = await GetJsonAsync(client, otherToken, "/api/v1/billboards?mine=true");
        Assert.Empty(otherMine.EnumerateArray());

        var groupResponse = await PostJsonAsync(client, adminToken, "/api/v1/admin/billboard-groups",
            new { name = "Моя группа" });
        var group = await groupResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = group.GetProperty("id").GetGuid();
        await PostJsonAsync(client, adminToken, $"/api/v1/admin/billboard-groups/{groupId}/billboards",
            new { billboardIds = new[] { billboardId } });

        var myGroups = await GetJsonAsync(client, clientToken, "/api/v1/billboard-groups?mine=true");
        Assert.Single(myGroups.EnumerateArray());
        var otherGroups = await GetJsonAsync(client, otherToken, "/api/v1/billboard-groups?mine=true");
        Assert.Empty(otherGroups.EnumerateArray());
    }

    [Fact]
    public async Task Admin_can_create_and_delete_user()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);

        var created = await PostJsonAsync(client, adminToken, "/api/v1/admin/users", new
        {
            email = "new-user@test.local",
            password = "NewUser_123",
            displayName = "Новый пользователь",
            role = "Client"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = await created.Content.ReadFromJsonAsync<JsonElement>();
        var userId = user.GetProperty("id").GetGuid();

        var login = await client.PostAsJsonAsync("/api/v1/account/login",
            new { email = "new-user@test.local", password = "NewUser_123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/admin/users/{userId}");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var deleted = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var afterDelete = await client.PostAsJsonAsync("/api/v1/account/login",
            new { email = "new-user@test.local", password = "NewUser_123" });
        Assert.Equal(HttpStatusCode.Unauthorized, afterDelete.StatusCode);
    }

    // ---------- вспомогательные ----------

    [Fact]
    public async Task Local_storage_manifest_url_is_served_over_http_with_signed_token()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "stream@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);
        var (billboardId, deviceToken) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, clientToken);
        await TopUpAsync(client, clientToken, 20m, "stream-topup");
        var placementId = await CreatePlacementAsync(client, clientToken, videoId, billboardId);
        var published = await PostJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/devices/{billboardId}/manifest");
        manifestRequest.Headers.Add("X-Device-Token", deviceToken);
        using var manifestResponse = await client.SendAsync(manifestRequest);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);

        var manifest = await manifestResponse.Content.ReadFromJsonAsync<JsonElement>();
        var file = Assert.Single(manifest.GetProperty("files").EnumerateArray());
        var url = file.GetProperty("downloadUrl").GetString()!;
        Assert.StartsWith("http://", url);
        Assert.Contains("/stream?token=", url);

        using var download = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(2048, (await download.Content.ReadAsByteArrayAsync()).Length);

        using var badToken = await client.GetAsync($"/api/v1/videos/{videoId}/stream?token=bad");
        Assert.Equal(HttpStatusCode.Unauthorized, badToken.StatusCode);
    }

    [Fact]
    public async Task Disabled_billboard_stops_manifest_and_rejects_playback()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "disabled@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);
        var (billboardId, deviceToken) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, clientToken);
        await TopUpAsync(client, clientToken, 20m, "disabled-topup");
        var placementId = await CreatePlacementAsync(client, clientToken, videoId, billboardId);
        await PostJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}/publish", null);

        var disabled = await PostJsonAsync(client, adminToken, $"/api/v1/admin/billboards/{billboardId}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/devices/{billboardId}/manifest");
        manifestRequest.Headers.Add("X-Device-Token", deviceToken);
        using var manifestResponse = await client.SendAsync(manifestRequest);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        var manifest = await manifestResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(manifest.GetProperty("files").EnumerateArray());

        using var playback = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/devices/{billboardId}/playback-events")
        {
            Content = JsonContent.Create(new
            {
                events = new[]
                {
                    new
                    {
                        idempotencyKey = "disabled-1",
                        placementId,
                        videoAssetId = videoId,
                        startedAt = DateTimeOffset.UtcNow,
                        durationSeconds = 15,
                        result = "Completed"
                    }
                }
            })
        };
        playback.Headers.Add("X-Device-Token", deviceToken);
        using var playbackResponse = await client.SendAsync(playback);
        Assert.Equal(HttpStatusCode.Conflict, playbackResponse.StatusCode);
        var error = await playbackResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BILLBOARD_DISABLED", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_can_pause_and_resume_client_placement()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "paused@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);
        var (billboardId, _) = await CreateBillboardAsync(client, adminToken);
        var videoId = await UploadVideoAsync(client, clientToken);
        await TopUpAsync(client, clientToken, 20m, "pause-topup");
        var placementId = await CreatePlacementAsync(client, clientToken, videoId, billboardId);
        await PostJsonAsync(client, clientToken, $"/api/v1/placements/{placementId}/publish", null);

        using var pause = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/placements/{placementId}/pause");
        pause.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var paused = await client.SendAsync(pause);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("Paused", (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        using var resume = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/placements/{placementId}/resume");
        resume.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var resumed = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal("Scheduled", (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Deactivated_user_loses_existing_bearer_token()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var clientToken = await RegisterAndLoginAsync(client, "blocked@test.local");
        var adminToken = await LoginAsync(client, ApiFactory.SeedAdminEmail, ApiFactory.SeedAdminPassword);
        var account = await GetJsonAsync(client, clientToken, "/api/v1/account/me");
        var userId = account.GetProperty("id").GetGuid();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/users/{userId}/active")
        {
            Content = JsonContent.Create(new { isActive = false })
        };
        patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var patched = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clientToken);
        using var response = await client.SendAsync(me);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("USER_INACTIVE", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Empty_group_returns_group_is_empty_code()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var clientToken = await RegisterAndLoginAsync(client, "empty-group@test.local");

        var response = await PostJsonAsync(client, clientToken, "/api/v1/placement-groups/validate", new
        {
            billboardIds = Array.Empty<Guid>(),
            template = new
            {
                videoAssetId = Guid.NewGuid(),
                startDate = "2026-11-10",
                endDate = "2026-11-10",
                daysOfWeek = new[] { "Monday" },
                startTime = "09:00:00",
                endTime = "18:00:00",
                minimumPlaysPerHour = 1,
                videoDurationSeconds = 15
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("GROUP_IS_EMPTY", error.GetProperty("code").GetString());
    }

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
