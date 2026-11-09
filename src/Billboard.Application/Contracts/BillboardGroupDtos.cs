using System.ComponentModel.DataAnnotations;

namespace Billboard.Application.Contracts;

/// <summary>Группа билбордов: удобно назначать одно размещение на несколько устройств.</summary>
public record BillboardGroupDto(
    Guid Id,
    string Name,
    IReadOnlyList<BillboardDto> Billboards);

public record BillboardGroupUpsertRequest(
    [Required, StringLength(100)] string Name);

public record BillboardGroupMembersRequest(
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<Guid> BillboardIds);
