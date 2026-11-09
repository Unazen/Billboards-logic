using Billboard.Api.Infrastructure;
using Billboard.Application.Contracts;
using Billboard.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Billboard.Api.Controllers;

[ApiController]
[Route("api/v1/account")]
public class AccountController(UserManager<ApplicationUser> users, ITariffService tariffs) : ApiControllerBase
{
    /// <summary>Регистрация клиента. Роль Admin выдаётся только администратором или seed-конфигурацией.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName.Trim()
        };

        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new
            {
                code = "REGISTRATION_FAILED",
                message = "Не удалось зарегистрироваться.",
                details = result.Errors.Select(e => e.Description)
            });

        await users.AddToRoleAsync(user, DbSeeder.ClientRole);
        return Created("/api/v1/account/me", new { user.Id, user.Email });
    }

    /// <summary>Вход. В ответе bearer-токен (accessToken) для заголовка Authorization.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, [FromServices] SignInManager<ApplicationUser> signIn)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive)
            return Unauthorized(new { code = "INVALID_CREDENTIALS", message = "Неверный email или пароль." });

        signIn.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
            return Unauthorized(new { code = "INVALID_CREDENTIALS", message = "Неверный email или пароль." });

        // Токен уже записан в ответ обработчиком bearer-схемы.
        return new EmptyResult();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AccountDto>> Me(CancellationToken ct)
    {
        var user = await users.FindByIdAsync(UserId.ToString());
        if (user is null) return NotFound();

        var plan = await tariffs.GetEffectivePlanAsync(user.Id, ct);
        var roles = await users.GetRolesAsync(user);
        return Ok(new AccountDto(user.Id, user.Email ?? "", user.DisplayName, roles.ToList(), plan.Name));
    }
}

[ApiController]
[Route("api/v1/tariffs")]
public class TariffsController(ITariffService tariffs) : ApiControllerBase
{
    /// <summary>Публичный список активных тарифов.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<TariffDto>>> List(CancellationToken ct) =>
        Ok(await tariffs.ListAsync(true, ct));

    /// <summary>Действующий тариф текущего пользователя.</summary>
    [HttpGet("current")]
    [Authorize]
    public async Task<ActionResult<TariffDto>> Current(CancellationToken ct) =>
        Ok(Billboard.Infrastructure.Services.TariffService.Map(await tariffs.GetEffectivePlanAsync(UserId, ct)));
}

[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/admin/tariffs")]
public class AdminTariffsController(ITariffService tariffs) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TariffDto>>> List(CancellationToken ct) =>
        Ok(await tariffs.ListAsync(false, ct));

    [HttpPost]
    public async Task<ActionResult<TariffDto>> Create([FromBody] TariffUpsertRequest request, CancellationToken ct)
    {
        var created = await tariffs.CreateAsync(request, ct);
        return Created($"/api/v1/admin/tariffs/{created.Id}", created);
    }

    /// <summary>Лимиты меняются без правки кода. Принимает полный объект тарифа.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<TariffDto>> Update(Guid id, [FromBody] TariffUpsertRequest request, CancellationToken ct) =>
        Ok(await tariffs.UpdateAsync(id, request, ct));
}

[ApiController]
[Authorize]
[Route("api/v1/billboards")]
public class BillboardsController(IBillboardService billboards) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BillboardDto>>> List(CancellationToken ct) =>
        Ok(await billboards.ListAsync(false, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BillboardDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await billboards.GetAsync(id, ct));
}

[ApiController]
[Authorize(Policy = Policies.Admin)]
[Route("api/v1/admin/billboards")]
public class AdminBillboardsController(IBillboardService billboards) : ApiControllerBase
{
    /// <summary>Полный список, включая отключённые, со статусом и временем последнего соединения.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BillboardDto>>> List(CancellationToken ct) =>
        Ok(await billboards.ListAsync(true, ct));

    /// <summary>Создаёт билборд. Токен устройства возвращается один раз — передайте его симулятору.</summary>
    [HttpPost]
    public async Task<ActionResult<BillboardCreatedDto>> Create([FromBody] BillboardUpsertRequest request, CancellationToken ct)
    {
        var created = await billboards.CreateAsync(request, ct);
        return Created($"/api/v1/billboards/{created.Billboard.Id}", created);
    }

    /// <summary>Принимает полный объект билборда.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<BillboardDto>> Update(Guid id, [FromBody] BillboardUpsertRequest request, CancellationToken ct) =>
        Ok(await billboards.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/disable")]
    public async Task<ActionResult<BillboardDto>> Disable(Guid id, CancellationToken ct) =>
        Ok(await billboards.DisableAsync(id, ct));

    [HttpPost("{id:guid}/rotate-device-token")]
    public async Task<ActionResult<BillboardCreatedDto>> RotateToken(Guid id, CancellationToken ct) =>
        Ok(await billboards.RotateDeviceTokenAsync(id, ct));
}
