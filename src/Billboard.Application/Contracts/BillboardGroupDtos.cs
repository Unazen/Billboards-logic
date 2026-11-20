using System.ComponentModel.DataAnnotations;

namespace Billboard.Application.Contracts;

/// <summary>Группа билбордов: удобно назначать одно размещение на несколько устройств.</summary>
public record BillboardGroupDto(
    Guid Id,
    string Name,
    Guid? OwnerId,
    IReadOnlyList<BillboardDto> Billboards);

public record BillboardGroupUpsertRequest(
    [Required, StringLength(100)] string Name,
    Guid? OwnerId = null);

public record BillboardGroupMembersRequest(
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<Guid> BillboardIds);
