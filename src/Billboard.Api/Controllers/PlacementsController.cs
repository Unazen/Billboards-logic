using Billboard.Api.Infrastructure;
using Billboard.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Billboard.Api.Controllers;

/// <summary>
/// Размещения: черновик, проверка расписания, публикация с резервом бюджета,
/// пауза, возобновление и отмена с возвратом неиспользованного резерва.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/placements")]
public class PlacementsController(IPlacementService placements) : ApiControllerBase
{
    /// <summary>Список размещений: клиенту — свои, администратору — все (?all=true).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlacementDto>>> List(
        [FromQuery] string? status, [FromQuery] bool all, CancellationToken ct) =>
        Ok(await placements.ListAsync(UserId, all && IsAdmin, status, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PlacementDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await placements.GetAsync(UserId, IsAdmin, id, ct));

    /// <summary>Создаёт черновик. Владелец берётся из токена, а не из тела запроса.</summary>
    [HttpPost]
    public async Task<ActionResult<PlacementDto>> Create([FromBody] PlacementUpsertRequest request, CancellationToken ct)
    {
        var created = await placements.CreateAsync(UserId, request, ct);
        return Created($"/api/v1/placements/{created.Id}", created);
    }

    /// <summary>Изменение черновика или размещения, ожидающего оплаты.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<PlacementDto>> Update(Guid id, [FromBody] PlacementUpsertRequest request, CancellationToken ct) =>
        Ok(await placements.UpdateAsync(UserId, id, request, ct));

    /// <summary>Проверка до сохранения: доступность времени, стоимость и альтернативные окна.</summary>
    [HttpPost("validate")]
    public async Task<ActionResult<PlacementValidationResultDto>> Validate(
        [FromBody] PlacementUpsertRequest request, CancellationToken ct) =>
        Ok(await placements.ValidateAsync(UserId, request, ct));

    [HttpGet("{id:guid}/alternatives")]
    public async Task<ActionResult<IReadOnlyList<AlternativeWindowDto>>> Alternatives(
        Guid id, [FromQuery] int max = 3, CancellationToken ct = default) =>
        Ok(await placements.GetAlternativesAsync(UserId, IsAdmin, id, max, ct));

    /// <summary>
    /// Публикация: проверка ёмкости, резерв бюджета, статус Scheduled.
    /// При нехватке денег — 409 INSUFFICIENT_FUNDS, размещение остаётся в PendingPayment.
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<PublishPlacementResultDto>> Publish(
        Guid id, [FromQuery] int billboardCount = 1, CancellationToken ct = default) =>
        Ok(await placements.PublishAsync(UserId, IsAdmin, id, billboardCount, ct));

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<PlacementDto>> Pause(Guid id, CancellationToken ct) =>
        Ok(await placements.PauseAsync(UserId, IsAdmin, id, ct));

    [HttpPost("{id:guid}/resume")]
    public async Task<ActionResult<PlacementDto>> Resume(Guid id, CancellationToken ct) =>
        Ok(await placements.ResumeAsync(UserId, IsAdmin, id, ct));

    /// <summary>Отмена: неиспользованный резерв возвращается на баланс.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<PlacementDto>> Cancel(Guid id, CancellationToken ct) =>
        Ok(await placements.CancelAsync(UserId, IsAdmin, id, ct));
}
