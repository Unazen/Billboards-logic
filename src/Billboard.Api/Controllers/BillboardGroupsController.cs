using Billboard.Api.Infrastructure;
using Billboard.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Billboard.Api.Controllers;

/// <summary>Каталог групп билбордов: доступен клиенту для выбора сразу нескольких устройств.</summary>
[ApiController]
[Authorize]
[Route("api/v1/billboard-groups")]
public class BillboardGroupsController(IBillboardGroupService groups) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BillboardGroupDto>>> List(CancellationToken ct) =>
        Ok(await groups.ListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BillboardGroupDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await groups.GetAsync(id, ct));
}

/// <summary>Управление группами билбордов (только администратор).</summary>
[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/admin/billboard-groups")]
public class AdminBillboardGroupsController(IBillboardGroupService groups) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BillboardGroupDto>>> List(CancellationToken ct) =>
        Ok(await groups.ListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BillboardGroupDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await groups.GetAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<BillboardGroupDto>> Create(
        [FromBody] BillboardGroupUpsertRequest request, CancellationToken ct)
    {
        var created = await groups.CreateAsync(request, ct);
        return Created($"/api/v1/admin/billboard-groups/{created.Id}", created);
    }

    /// <summary>Переименование группы.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<BillboardGroupDto>> Rename(
        Guid id, [FromBody] BillboardGroupUpsertRequest request, CancellationToken ct) =>
        Ok(await groups.RenameAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await groups.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Добавить билборды в группу. Повторное добавление молча игнорируется.</summary>
    [HttpPost("{id:guid}/billboards")]
    public async Task<ActionResult<BillboardGroupDto>> AddBillboards(
        Guid id, [FromBody] BillboardGroupMembersRequest request, CancellationToken ct) =>
        Ok(await groups.AddBillboardsAsync(id, request, ct));

    [HttpDelete("{id:guid}/billboards/{billboardId:guid}")]
    public async Task<ActionResult<BillboardGroupDto>> RemoveBillboard(
        Guid id, Guid billboardId, CancellationToken ct) =>
        Ok(await groups.RemoveBillboardAsync(id, billboardId, ct));
}
