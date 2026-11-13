using Billboard.Api.Infrastructure;
using Billboard.Application.Contracts;
using Billboard.Infrastructure.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Billboard.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/videos")]
public class VideosController(IVideoService videos, VideoStreamTokens streamTokens) : ApiControllerBase
{
    /// <summary>Медиатека текущего клиента (администратор может запросить все: ?all=true).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VideoDto>>> List([FromQuery] bool all, CancellationToken ct) =>
        Ok(await videos.ListAsync(UserId, all && IsAdmin, ct));

    /// <summary>Шаг 1. Создать задачу загрузки; сервер проверяет формат, лимиты тарифа и размер.</summary>
    [HttpPost("uploads")]
    public async Task<ActionResult<UploadInitDto>> CreateUpload([FromBody] CreateUploadRequest request, CancellationToken ct)
    {
        var result = await videos.CreateUploadAsync(UserId, request, ct);
        return Created($"/api/v1/videos/{result.VideoId}", result);
    }

    /// <summary>
    /// Шаг 2. Передать файл «как есть» телом запроса (Content-Type: application/octet-stream).
    /// Файл не проходит через JSON; лимит размера контролируется по тарифу на лету.
    /// </summary>
    [HttpPut("{id:guid}/content")]
    [DisableRequestSizeLimit]
    public async Task<ActionResult<VideoDto>> UploadContent(Guid id, CancellationToken ct) =>
        Ok(await videos.UploadContentAsync(UserId, IsAdmin, id, Request.Body, ct));

    /// <summary>Импорт видео по ссылке (с защитой от обращений во внутренние сети).</summary>
    [HttpPost("import-url")]
    public async Task<ActionResult<VideoDto>> ImportUrl([FromBody] ImportUrlRequest request, CancellationToken ct)
    {
        var result = await videos.ImportFromUrlAsync(UserId, request.Url, ct);
        return Created($"/api/v1/videos/{result.Id}", result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<VideoDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await videos.GetAsync(UserId, IsAdmin, id, ct));

    /// <summary>Удаление запрещено (409 VIDEO_IN_USE), пока видео используется активными размещениями.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await videos.DeleteAsync(UserId, IsAdmin, id, ct);
        return NoContent();
    }

    /// <summary>Временная ссылка на скачивание оригинала.</summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct) =>
        Ok(new { url = (await videos.GetDownloadUrlAsync(UserId, IsAdmin, id, ct)).ToString() });

    /// <summary>
    /// Отдача файла при локальном хранилище. Доступ по подписанной ссылке из манифеста
    /// (presigned URL S3 здесь недоступен), поэтому обычная авторизация не требуется.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}/stream")]
    public async Task<IActionResult> Stream(Guid id, [FromQuery] string? token, CancellationToken ct)
    {
        if (!streamTokens.TryValidate(token, id, out _))
            return Unauthorized(new { code = "INVALID_STREAM_TOKEN", message = "Ссылка недействительна или истекла." });

        var file = await videos.OpenStreamAsync(id, ct);
        return File(file.Content, file.ContentType, enableRangeProcessing: true);
    }
}
