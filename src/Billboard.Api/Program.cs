using Billboard.Api.Infrastructure;
using Billboard.Infrastructure;
using Billboard.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// ---------- Инфраструктура: PostgreSQL, MinIO, сервисы домена ----------
builder.Services.AddBillboardInfrastructure(builder.Configuration);

// Ключи защиты данных (подпись bearer-токенов) переживают перезапуск контейнера, если задан путь.
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// ---------- Identity: пользователи и роли Admin / Client ----------
builder.Services
    .AddIdentityCore<ApplicationUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 6;
        o.Password.RequireDigit = true;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager();

// ---------- Аутентификация ----------
// Пользователи: bearer-токен после POST /api/v1/account/login.
// Симуляторы: заголовок X-Device-Token (отдельная схема, без прав администратора).
builder.Services
    .AddAuthentication(IdentityConstants.BearerScheme)
    .AddBearerToken(IdentityConstants.BearerScheme)
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenHandler>(DeviceTokenHandler.SchemeName, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, p => p
        .AddAuthenticationSchemes(IdentityConstants.BearerScheme)
        .RequireRole(DbSeeder.AdminRole))
    .AddPolicy(Policies.Device, p => p
        .AddAuthenticationSchemes(DeviceTokenHandler.SchemeName)
        .RequireClaim(DeviceTokenHandler.BillboardClaim));

// ---------- MVC ----------
builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        // Ошибки валидации — в едином формате { code, message, details }, HTTP 400.
        o.InvalidModelStateResponseFactory = ctx => new BadRequestObjectResult(new
        {
            code = "VALIDATION_FAILED",
            message = "Некорректные данные запроса.",
            details = ctx.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .ToDictionary(e => e.Key, e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray())
        });
    });

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();

// OpenAPI-документ нужен и на защите проекта: /openapi/v1.json
app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

// Схема БД (миграции или EnsureCreated), бакет MinIO, тарифы, роли, администратор из конфигурации.
await DbSeeder.InitializeAsync(app.Services);

app.Run();

// Нужен интеграционным тестам (WebApplicationFactory<Program>).
public partial class Program;
