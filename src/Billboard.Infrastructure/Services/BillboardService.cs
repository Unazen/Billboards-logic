using System.Security.Cryptography;
using System.Text;
using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

public sealed class BillboardService(AppDbContext db) : IBillboardService
{
    public async Task<IReadOnlyList<BillboardDto>> ListAsync(
        bool includeDisabled, Guid? ownerUserId = null, CancellationToken ct = default)
    {
        var query = db.Billboards.AsNoTracking().AsQueryable();
        if (!includeDisabled) query = query.Where(b => b.Status != BillboardStatus.Disabled);

        // «Мои» устройства: владение или действующее/прошлое размещение пользователя.
        if (ownerUserId is { } userId)
            query = query.Where(b => b.OwnerId == userId
                                     || db.AdPlacements.Any(p => p.BillboardId == b.Id && p.OwnerId == userId));

        var items = await query.OrderBy(b => b.Name).ToListAsync(ct);
        return items.Select(Map).ToList();
    }

    public async Task<BillboardDto> GetAsync(Guid id, CancellationToken ct = default) =>
        Map(await db.Billboards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw AppException.NotFound("Билборд"));

    public async Task<BillboardCreatedDto> CreateAsync(BillboardUpsertRequest r, CancellationToken ct = default)
    {
        await EnsureOwnerExistsAsync(r.OwnerId, ct);
        var billboard = new BillboardDevice { Id = Guid.NewGuid() };
        Apply(billboard, r);

        var token = GenerateToken(billboard.Id);
        billboard.DeviceTokenHash = HashToken(token);

        db.Billboards.Add(billboard);
        SystemLogWriter.Add(db, null, "BillboardCreated", nameof(BillboardDevice), billboard.Id);
        await db.SaveChangesAsync(ct);
        return new BillboardCreatedDto(Map(billboard), token);
    }

    public async Task<BillboardDto> UpdateAsync(Guid id, BillboardUpsertRequest r, CancellationToken ct = default)
    {
        await EnsureOwnerExistsAsync(r.OwnerId, ct);
        var billboard = await db.Billboards.FirstOrDefaultAsync(b => b.Id == id, ct)
                        ?? throw AppException.NotFound("Билборд");
        Apply(billboard, r);
        SystemLogWriter.Add(db, null, "BillboardUpdated", nameof(BillboardDevice), id);
        await db.SaveChangesAsync(ct);
        return Map(billboard);
    }

    public async Task<BillboardDto> DisableAsync(Guid id, CancellationToken ct = default)
    {
        var billboard = await db.Billboards.FirstOrDefaultAsync(b => b.Id == id, ct)
                        ?? throw AppException.NotFound("Билборд");
        billboard.Status = BillboardStatus.Disabled;

        // Устройство больше не играет: останавливаем активные размещения, чтобы показы и списания прекратились.
        var playing = new[]
        {
            PlacementStatus.Scheduled, PlacementStatus.Downloading,
            PlacementStatus.Ready, PlacementStatus.Active
        };
        var placements = await db.AdPlacements
            .Where(p => p.BillboardId == id && playing.Contains(p.Status))
            .ToListAsync(ct);
        foreach (var placement in placements)
        {
            placement.Status = PlacementStatus.Paused;
            placement.PauseReason = "BillboardDisabled";
            SystemLogWriter.Add(db, null, "PlacementPaused", nameof(AdPlacement), placement.Id, "WARN",
                "BillboardDisabled");
        }

        SystemLogWriter.Add(db, null, "BillboardDisabled", nameof(BillboardDevice), id);
        await db.SaveChangesAsync(ct);
        return Map(billboard);
    }

    public async Task<BillboardCreatedDto> RotateDeviceTokenAsync(Guid id, CancellationToken ct = default)
    {
        var billboard = await db.Billboards.FirstOrDefaultAsync(b => b.Id == id, ct)
                        ?? throw AppException.NotFound("Билборд");
        var token = GenerateToken(id);
        billboard.DeviceTokenHash = HashToken(token);
        SystemLogWriter.Add(db, null, "DeviceTokenRotated", nameof(BillboardDevice), id);
        await db.SaveChangesAsync(ct);
        return new BillboardCreatedDto(Map(billboard), token);
    }

    public async Task HeartbeatAsync(Guid billboardId, HeartbeatRequest request, CancellationToken ct = default)
    {
        var billboard = await db.Billboards.FirstOrDefaultAsync(b => b.Id == billboardId, ct)
                        ?? throw AppException.NotFound("Билборд");

        var now = DateTimeOffset.UtcNow;
        var status = Enum.TryParse<BillboardStatus>(request.Status, true, out var parsed) ? parsed : BillboardStatus.Online;

        billboard.LastSeenAt = now;
        // Отключённый администратором билборд сам себя «включить» не может.
        if (billboard.Status != BillboardStatus.Disabled)
            billboard.Status = status == BillboardStatus.Disabled ? BillboardStatus.Online : status;

        db.DeviceHeartbeats.Add(new DeviceHeartbeat
        {
            Id = Guid.NewGuid(),
            BillboardId = billboardId,
            ReceivedAt = now,
            SimulatorVersion = request.SimulatorVersion,
            Status = billboard.Status
        });
        await db.SaveChangesAsync(ct);
    }

    // ---------- токены устройств ----------

    /// <summary>Формат токена: "{billboardId:N}.{случайные 32 байта в base64url}".</summary>
    public static string GenerateToken(Guid billboardId) =>
        $"{billboardId:N}.{Base64Url(RandomNumberGenerator.GetBytes(32))}";

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool TryParseBillboardId(string token, out Guid billboardId)
    {
        billboardId = Guid.Empty;
        var dot = token.IndexOf('.');
        return dot > 0 && Guid.TryParseExact(token[..dot], "N", out billboardId);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void Apply(BillboardDevice b, BillboardUpsertRequest r)
    {
        b.OwnerId = r.OwnerId;
        b.Name = r.Name.Trim();
        b.Address = r.Address.Trim();
        b.Latitude = r.Latitude;
        b.Longitude = r.Longitude;
        if (r.CapacityBytes is > 0) b.CapacityBytes = r.CapacityBytes.Value;
        if (r.AdvertCapacitySecondsPerHour is > 0 and <= 3600) b.AdvertCapacitySecondsPerHour = r.AdvertCapacitySecondsPerHour.Value;
        b.PricePerPlay = r.PricePerPlay;
        if (!string.IsNullOrWhiteSpace(r.TimeZoneId)) b.TimeZoneId = r.TimeZoneId.Trim();
    }

    private async Task EnsureOwnerExistsAsync(Guid? ownerId, CancellationToken ct)
    {
        if (ownerId is { } id && !await db.Users.AnyAsync(u => u.Id == id, ct))
            throw AppException.NotFound("Владелец билборда");
    }

    public static BillboardDto Map(BillboardDevice b) => new(
        b.Id, b.Name, b.Address, b.Latitude, b.Longitude, b.CapacityBytes,
        b.AdvertCapacitySecondsPerHour, b.Status.ToString(), b.LastSeenAt, b.PricePerPlay, b.TimeZoneId,
        b.OwnerId);
}
