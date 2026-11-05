using System.Globalization;
using System.Text;
using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

public sealed class AdminService(AppDbContext db, UserManager<ApplicationUser> users) : IAdminService
{
    private static readonly string[] AllowedRoles = [DbSeeder.AdminRole, DbSeeder.ClientRole];
    private const int MaxLogExport = 50_000;

    public async Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(int skip, int take, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);

        var list = await users.Users.AsNoTracking()
            .OrderBy(u => u.Email)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

        var result = new List<AdminUserDto>(list.Count);
        foreach (var u in list)
            result.Add(await MapAsync(u));
        return result;
    }

    public async Task<AdminUserDto> SetUserActiveAsync(Guid actorId, Guid userId, bool isActive, CancellationToken ct = default)
    {
        if (actorId == userId && !isActive)
            throw AppException.Validation("CANNOT_DEACTIVATE_SELF", "Нельзя заблокировать собственную учётную запись.");

        var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.NotFound("Пользователь");
        user.IsActive = isActive;
        var res = await users.UpdateAsync(user);
        if (!res.Succeeded)
            throw AppException.Validation("USER_UPDATE_FAILED", string.Join("; ", res.Errors.Select(e => e.Description)));

        db.SystemLogs.Add(new Domain.Entities.SystemLog
        {
            Id = Guid.NewGuid(), UserId = actorId, Action = isActive ? "UserActivated" : "UserDeactivated",
            EntityType = "User", EntityId = userId.ToString(), Result = "OK", CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return await MapAsync(user);
    }

    public async Task<AdminUserDto> SetUserRoleAsync(Guid actorId, Guid userId, string role, CancellationToken ct = default)
    {
        var normalized = AllowedRoles.FirstOrDefault(r => r.Equals(role?.Trim(), StringComparison.OrdinalIgnoreCase))
                         ?? throw AppException.Validation("INVALID_ROLE", "Допустимые роли: Admin, Client.");

        if (actorId == userId && normalized != DbSeeder.AdminRole)
            throw AppException.Validation("CANNOT_DEMOTE_SELF", "Нельзя лишить себя роли администратора.");

        var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.NotFound("Пользователь");

        var current = await users.GetRolesAsync(user);
        var toRemove = current.Where(r => r != normalized).ToList();
        if (toRemove.Count > 0)
            await users.RemoveFromRolesAsync(user, toRemove);
        if (!current.Contains(normalized))
            await users.AddToRoleAsync(user, normalized);

        db.SystemLogs.Add(new Domain.Entities.SystemLog
        {
            Id = Guid.NewGuid(), UserId = actorId, Action = "UserRoleChanged",
            EntityType = "User", EntityId = userId.ToString(), Result = "OK", Details = normalized,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return await MapAsync(user);
    }

    public async Task<IReadOnlyList<SystemLogDto>> QueryLogsAsync(SystemLogQuery q, CancellationToken ct = default)
    {
        var take = Math.Clamp(q.Take, 1, 500);
        var items = await Filter(q).OrderByDescending(l => l.CreatedAt)
            .Skip(Math.Max(0, q.Skip)).Take(take)
            .ToListAsync(ct);
        return items.Select(Map).ToList();
    }

    public async Task<string> ExportLogsCsvAsync(SystemLogQuery q, CancellationToken ct = default)
    {
        var items = await Filter(q).OrderByDescending(l => l.CreatedAt).Take(MaxLogExport).ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("Id,CreatedAt,UserId,Action,EntityType,EntityId,Result,Details");
        foreach (var l in items)
        {
            sb.Append(l.Id).Append(',')
              .Append(l.CreatedAt.ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(l.UserId).Append(',')
              .Append(Csv(l.Action)).Append(',')
              .Append(Csv(l.EntityType)).Append(',')
              .Append(Csv(l.EntityId)).Append(',')
              .Append(Csv(l.Result)).Append(',')
              .AppendLine(Csv(l.Details));
        }
        return sb.ToString();
    }

    public async Task<IReadOnlyList<AdminDeviceDto>> ListDevicesAsync(CancellationToken ct = default)
    {
        var billboards = await db.Billboards.AsNoTracking().OrderBy(b => b.Name).ToListAsync(ct);

        var cache = await db.DeviceCachedVideos.AsNoTracking()
            .GroupBy(c => c.BillboardId)
            .Select(g => new { g.Key, Bytes = g.Sum(c => c.SizeBytes) })
            .ToDictionaryAsync(x => x.Key, x => x.Bytes, ct);

        // Последняя версия симулятора берётся из самого свежего heartbeat каждого устройства.
        var heartbeats = await db.DeviceHeartbeats.AsNoTracking().ToListAsync(ct);
        var versions = heartbeats
            .GroupBy(h => h.BillboardId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.ReceivedAt).First().SimulatorVersion);

        return billboards.Select(b => new AdminDeviceDto(
            b.Id, b.Name, b.Status.ToString(), b.LastSeenAt,
            versions.GetValueOrDefault(b.Id),
            cache.GetValueOrDefault(b.Id),
            b.CapacityBytes)).ToList();
    }

    // ---------- helpers ----------

    private IQueryable<Domain.Entities.SystemLog> Filter(SystemLogQuery q)
    {
        var query = db.SystemLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Action)) query = query.Where(l => l.Action == q.Action);
        if (q.UserId is { } uid) query = query.Where(l => l.UserId == uid);
        if (q.From is { } from) query = query.Where(l => l.CreatedAt >= from);
        if (q.To is { } to) query = query.Where(l => l.CreatedAt <= to);
        return query;
    }

    private async Task<AdminUserDto> MapAsync(ApplicationUser u) =>
        new(u.Id, u.Email ?? "", u.DisplayName, u.IsActive, (await users.GetRolesAsync(u)).ToList(), u.CreatedAt);

    private static SystemLogDto Map(Domain.Entities.SystemLog l) =>
        new(l.Id, l.UserId, l.Action, l.EntityType, l.EntityId, l.Result, l.Details, l.CreatedAt);

    /// <summary>Экранирует значение для CSV и нейтрализует формулы (=, +, -, @) для Excel.</summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}

