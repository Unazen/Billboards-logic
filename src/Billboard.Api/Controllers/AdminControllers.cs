using System.Text;
using Billboard.Api.Infrastructure;
using Billboard.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Billboard.Api.Controllers;

[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/admin/users")]
public class AdminUsersController(IAdminService admin) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminUserDto>>> List(
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await admin.ListUsersAsync(skip, take, ct));

    /// <summary>Блокировка / разблокировка пользователя.</summary>
    [HttpPatch("{id:guid}/active")]
    public async Task<ActionResult<AdminUserDto>> SetActive(Guid id, [FromBody] SetUserActiveRequest request, CancellationToken ct) =>
        Ok(await admin.SetUserActiveAsync(UserId, id, request.IsActive, ct));

    [HttpPatch("{id:guid}/role")]
    public async Task<ActionResult<AdminUserDto>> SetRole(Guid id, [FromBody] SetUserRoleRequest request, CancellationToken ct) =>
        Ok(await admin.SetUserRoleAsync(UserId, id, request.Role, ct));
}

[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/admin/logs")]
public class AdminLogsController(IAdminService admin) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SystemLogDto>>> Query([FromQuery] SystemLogQuery query, CancellationToken ct) =>
        Ok(await admin.QueryLogsAsync(query, ct));

    /// <summary>Выгрузка системных журналов в CSV с теми же фильтрами.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] SystemLogQuery query, CancellationToken ct)
    {
        var csv = await admin.ExportLogsCsvAsync(query, ct);
        // BOM нужен, чтобы Excel корректно открыл кириллицу.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"system-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }
}

[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/admin/devices")]
public class AdminDevicesController(IAdminService admin) : ApiControllerBase
{
    /// <summary>Мониторинг всех устройств: статус, последнее соединение, занятость кэша.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminDeviceDto>>> List(CancellationToken ct) =>
        Ok(await admin.ListDevicesAsync(ct));
}
