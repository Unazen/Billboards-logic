using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Domain.Entities;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

/// <summary>
/// Группы билбордов: CRUD и состав участников. Групповое назначение размещений
/// (проверка всех устройств сразу) выполняет планировщик участника 2.
/// </summary>
public sealed class BillboardGroupService(AppDbContext db) : IBillboardGroupService
{
    private const int MaxMembers = 500;

    public async Task<IReadOnlyList<BillboardGroupDto>> ListAsync(Guid? ownerUserId = null, CancellationToken ct = default)
    {
        var groups = await Query()
            .OrderBy(g => g.Name)
            .ToListAsync(ct);

        if (ownerUserId is not { } userId)
            return groups.Select(Map).ToList();

        // «Мои группы»: группа принадлежит пользователю или содержит устройство,
        // которым он владеет либо которое арендует (есть его размещение).
        var myBillboardIds = await db.Billboards.AsNoTracking()
            .Where(b => b.OwnerId == userId
                        || db.AdPlacements.Any(p => p.BillboardId == b.Id && p.OwnerId == userId))
            .Select(b => b.Id)
            .ToListAsync(ct);

        return groups
            .Where(g => g.OwnerId == userId
                        || g.Members.Any(m => myBillboardIds.Contains(m.BillboardId)))
            .Select(Map)
            .ToList();
    }

    public async Task<BillboardGroupDto> GetAsync(Guid id, CancellationToken ct = default) =>
        Map(await LoadAsync(id, ct));

    public async Task<BillboardGroupDto> CreateAsync(BillboardGroupUpsertRequest request, CancellationToken ct = default)
    {
        var name = NormalizeName(request.Name);
        await EnsureNameFreeAsync(name, null, ct);
        await EnsureOwnerExistsAsync(request.OwnerId, ct);

        var group = new BillboardGroup { Id = Guid.NewGuid(), Name = name, OwnerId = request.OwnerId };
        db.BillboardGroups.Add(group);
        SystemLogWriter.Add(db, null, "BillboardGroupCreated", nameof(BillboardGroup), group.Id, "OK", name);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw AppException.Conflict("GROUP_NAME_EXISTS", "Группа с таким названием уже существует.");
        }
        return Map(group);
    }

    public async Task<BillboardGroupDto> RenameAsync(Guid id, BillboardGroupUpsertRequest request, CancellationToken ct = default)
    {
        var group = await LoadAsync(id, ct);
        var name = NormalizeName(request.Name);
        await EnsureNameFreeAsync(name, id, ct);

        group.Name = name;
        SystemLogWriter.Add(db, null, "BillboardGroupRenamed", nameof(BillboardGroup), group.Id, "OK", name);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw AppException.Conflict("GROUP_NAME_EXISTS", "Группа с таким названием уже существует.");
        }
        return Map(group);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var group = await LoadAsync(id, ct);

        // Состав группы удаляется явно: FK настроены как Restrict, историю не рвём.
        db.BillboardGroupMembers.RemoveRange(group.Members);
        db.BillboardGroups.Remove(group);
        SystemLogWriter.Add(db, null, "BillboardGroupDeleted", nameof(BillboardGroup), id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<BillboardGroupDto> AddBillboardsAsync(Guid id, BillboardGroupMembersRequest request, CancellationToken ct = default)
    {
        var group = await LoadAsync(id, ct);

        var ids = request.BillboardIds.Distinct().ToList();
        if (ids.Count == 0)
            throw AppException.Validation("EMPTY_GROUP_REQUEST", "Укажите хотя бы один билборд.");
        if (group.Members.Count + ids.Count > MaxMembers)
            throw AppException.Validation("GROUP_TOO_LARGE", $"В группе не может быть больше {MaxMembers} билбордов.");

        var existing = await db.Billboards.AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => b.Id)
            .ToListAsync(ct);

        var unknown = ids.Except(existing).ToList();
        if (unknown.Count > 0)
            throw AppException.Validation("UNKNOWN_BILLBOARD", "Некоторые билборды не найдены.",
                new { billboardIds = unknown });

        var alreadyInGroup = group.Members.Select(m => m.BillboardId).ToHashSet();
        foreach (var billboardId in ids.Where(x => !alreadyInGroup.Contains(x)))
            group.Members.Add(new BillboardGroupMember { BillboardGroupId = group.Id, BillboardId = billboardId });

        SystemLogWriter.Add(db, null, "BillboardGroupMembersAdded", nameof(BillboardGroup), group.Id, "OK",
            string.Join(",", ids));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<BillboardGroupDto> RemoveBillboardAsync(Guid id, Guid billboardId, CancellationToken ct = default)
    {
        var group = await LoadAsync(id, ct);
        var member = group.Members.FirstOrDefault(m => m.BillboardId == billboardId)
                     ?? throw AppException.NotFound("Билборд в группе");

        db.BillboardGroupMembers.Remove(member);
        SystemLogWriter.Add(db, null, "BillboardGroupMemberRemoved", nameof(BillboardGroup), group.Id, "OK",
            billboardId.ToString());
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------- вспомогательные ----------

    private IQueryable<BillboardGroup> Query() =>
        db.BillboardGroups.AsNoTracking()
            .Include(g => g.Members)
            .ThenInclude(m => m.Billboard);

    private async Task<BillboardGroup> LoadAsync(Guid id, CancellationToken ct) =>
        await db.BillboardGroups
            .Include(g => g.Members)
            .ThenInclude(m => m.Billboard)
            .FirstOrDefaultAsync(g => g.Id == id, ct)
        ?? throw AppException.NotFound("Группа билбордов");

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw AppException.Validation("INVALID_GROUP_NAME", "Название группы не может быть пустым.");
        return trimmed;
    }

    private async Task EnsureNameFreeAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var names = await db.BillboardGroups.AsNoTracking()
            .Where(g => exceptId == null || g.Id != exceptId)
            .Select(g => g.Name)
            .ToListAsync(ct);

        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw AppException.Conflict("GROUP_NAME_EXISTS", $"Группа «{name}» уже существует.");
    }

    private async Task EnsureOwnerExistsAsync(Guid? ownerId, CancellationToken ct)
    {
        if (ownerId is { } id && !await db.Users.AnyAsync(u => u.Id == id, ct))
            throw AppException.NotFound("Владелец группы");
    }

    private static BillboardGroupDto Map(BillboardGroup group) => new(
        group.Id,
        group.Name,
        group.OwnerId,
        group.Members
            .Where(m => m.Billboard is not null)
            .Select(m => BillboardService.Map(m.Billboard!))
            .OrderBy(b => b.Name)
            .ToList());
}
