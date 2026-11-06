namespace Billboard.Application.Common;

/// <summary>
/// Ожидаемая бизнес-ошибка. API превращает её в ответ
/// { "code", "message", "details" } с нужным HTTP-статусом.
/// </summary>
public class AppException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }
    public object? Details { get; }

    public AppException(string code, string message, int statusCode = 400, object? details = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Details = details;
    }

    public static AppException Validation(string code, string message, object? details = null) =>
        new(code, message, 400, details);

    public static AppException NotFound(string what) =>
        new("NOT_FOUND", $"{what}: не найдено.", 404);

    public static AppException Forbidden(string message = "Недостаточно прав.") =>
        new("FORBIDDEN", message, 403);

    public static AppException Conflict(string code, string message, object? details = null) =>
        new(code, message, 409, details);
}
