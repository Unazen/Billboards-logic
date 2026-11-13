using System.Security.Claims;
using Billboard.Application.Common;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Api.Infrastructure;

/// <summary>
/// Блокировка пользователя должна действовать сразу, а не только при следующем входе:
/// bearer-токен живёт до истечения, поэтому проверяем IsActive на каждом запросе.
/// </summary>
public sealed class ActiveUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && !u.IsActive))
        {
            throw new AppException("USER_INACTIVE", "Учётная запись заблокирована.", StatusCodes.Status401Unauthorized);
        }

        await next(context);
    }
}
