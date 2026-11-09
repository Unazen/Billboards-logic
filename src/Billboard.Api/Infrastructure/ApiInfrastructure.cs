using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Billboard.Application.Common;
using Billboard.Infrastructure.Persistence;
using Billboard.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Billboard.Api.Infrastructure;

public static class Policies
{
    public const string Admin = "Admin";
    public const string Device = "Device";
}

public abstract class ApiControllerBase : ControllerBase
{
    protected Guid UserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new AppException("UNAUTHORIZED", "Пользователь не определён.", 401);

    protected bool IsAdmin => User.IsInRole(DbSeeder.AdminRole);
}

/// <summary>Превращает AppException и типовые ошибки БД в единый формат { code, message, details }.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            await WriteAsync(context, ex.StatusCode, ex.Code, ex.Message, ex.Details);
        }
        catch (DbUpdateConcurrencyException)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, "CONCURRENT_UPDATE",
                "Данные изменились одновременно. Повторите запрос.", null);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Клиент сам оборвал запрос.
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Необработанная ошибка {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "INTERNAL_ERROR",
                "Внутренняя ошибка сервера.", null);
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string code, string message, object? details)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { code, message, details });
    }
}

/// <summary>
/// Аутентификация симулятора: заголовок X-Device-Token: "{billboardId}.{секрет}".
/// В БД хранится только SHA-256 токена. Результат — claim billboard_id.
/// </summary>
public sealed class DeviceTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Device";
    public const string HeaderName = "X-Device-Token";
    public const string BillboardClaim = "billboard_id";

    private readonly AppDbContext _db;

    public DeviceTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, AppDbContext db) : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
            return AuthenticateResult.NoResult();

        var token = header.ToString();
        if (!BillboardService.TryParseBillboardId(token, out var billboardId))
            return AuthenticateResult.Fail("Неверный формат токена.");

        var stored = await _db.Billboards.AsNoTracking()
            .Where(b => b.Id == billboardId)
            .Select(b => b.DeviceTokenHash)
            .FirstOrDefaultAsync();
        if (stored is null)
            return AuthenticateResult.Fail("Неверный токен.");

        var actual = Encoding.ASCII.GetBytes(BillboardService.HashToken(token));
        var expected = Encoding.ASCII.GetBytes(stored);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            return AuthenticateResult.Fail("Неверный токен.");

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, billboardId.ToString()),
            new Claim(BillboardClaim, billboardId.ToString()),
            new Claim(ClaimTypes.Role, "Device")
        }, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
