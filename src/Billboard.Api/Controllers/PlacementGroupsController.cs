using Billboard.Api.Infrastructure;
using Billboard.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Billboard.Api.Controllers;

/// <summary>
/// Групповое назначение расписания (контракт P2): проверка всех устройств группы
/// и атомарное создание черновиков с резервированием бюджета.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/placement-groups")]
public class PlacementGroupsController(IPlacementGroupService groups) : ApiControllerBase
{
    /// <summary>Проверка шаблона расписания на всех билбордах группы.</summary>
    [HttpPost("validate")]
    public async Task<ActionResult<GroupScheduleValidationDto>> Validate(
        [FromBody] PlacementGroupRequestDto request, CancellationToken ct) =>
        Ok(await groups.ValidateAsync(UserId, request, ct));

    /// <summary>
    /// Создание черновиков «всё или ничего»: при конфликте любого устройства или
    /// нехватке бюджета не создаётся ни одного размещения.
    /// </summary>
    [HttpPost("drafts")]
    public async Task<ActionResult<GroupPlacementOperationDto>> Drafts(
        [FromBody] PlacementGroupRequestDto request, CancellationToken ct)
    {
        var result = await groups.CreateDraftsAsync(UserId, request, ct);
        if (result.Success) return Ok(result);

        return result.ErrorCode == "GROUP_IS_EMPTY"
            ? BadRequest(result)
            : Conflict(result);
    }
}
