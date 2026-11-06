using Billboard.Api.Infrastructure;
using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Billboard.Api.Controllers;

/// <summary>
/// API симулятора билборда. Аутентификация — заголовок X-Device-Token.
/// Устройство может обращаться только к собственному {id}.
///
/// Здесь — серверная часть: манифест (что скачивать), отчёт о кэше, конфигурация и приём
/// событий показов. Сам симулятор (очередь, ротация, офлайн-режим) реализует участник 2.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.Device)]
[Route("api/v1/devices/{id:guid}")]
public class DevicesController(
    IBillboardService billboards,
    IPlaybackService playback,
    IDeviceService devices) : ApiControllerBase
{
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid id, [FromBody] HeartbeatRequest request, CancellationToken ct)
    {
        EnsureOwnDevice(id);
        await billboards.HeartbeatAsync(id, request, ct);
        return NoContent();
    }

    /// <summary>Список файлов, которые нужны устройству прямо сейчас, с временными ссылками.</summary>
    [HttpGet("manifest")]
    public async Task<ActionResult<DeviceManifestDto>> Manifest(Guid id, CancellationToken ct)
    {
        EnsureOwnDevice(id);
        return Ok(await devices.GetManifestAsync(id, ct));
    }

    /// <summary>Отчёт о том, что уже скачано и проверено. Используется для контроля кэша.</summary>
    [HttpPost("cache-reports")]
    public async Task<ActionResult<CacheReportResultDto>> CacheReport(
        Guid id, [FromBody] CacheReportRequest request, CancellationToken ct)
    {
        EnsureOwnDevice(id);
        return Ok(await devices.ReportCacheAsync(id, request.Items, ct));
    }

    /// <summary>Лимит памяти (по умолчанию 32 ГБ) и настройки устройства.</summary>
    [HttpGet("configuration")]
    public async Task<ActionResult<DeviceConfigurationDto>> Configuration(Guid id, CancellationToken ct)
    {
        EnsureOwnDevice(id);
        return Ok(await devices.GetConfigurationAsync(id, ct));
    }

    /// <summary>
    /// Пакет событий показов. Повторная отправка безопасна: дубликаты игнорируются
    /// и не приводят к повторному списанию.
    /// </summary>
    [HttpPost("playback-events")]
    public async Task<ActionResult<PlaybackEventsResultDto>> PlaybackEvents(
        Guid id, [FromBody] PlaybackEventsRequest request, CancellationToken ct)
    {
        EnsureOwnDevice(id);
        return Ok(await playback.IngestAsync(id, request.Events, ct));
    }

    private void EnsureOwnDevice(Guid id)
    {
        if (User.FindFirst(DeviceTokenHandler.BillboardClaim)?.Value != id.ToString())
            throw AppException.Forbidden("Токен устройства не относится к этому билборду.");
    }
}
