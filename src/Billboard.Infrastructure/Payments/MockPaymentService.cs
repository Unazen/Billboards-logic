using Billboard.Application.Contracts;

namespace Billboard.Infrastructure.Payments;

/// <summary>
/// Имитация платёжной системы для учебного проекта. Реальных платежей нет:
/// пополнение успешно, если не запрошена имитация отказа.
/// </summary>
public sealed class MockPaymentService : IPaymentGateway
{
    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
    {
        if (request.ForceFailure)
            return Task.FromResult(new PaymentResult(false, null, "Платёж отклонён (имитация отказа)."));
        if (request.Amount <= 0)
            return Task.FromResult(new PaymentResult(false, null, "Некорректная сумма платежа."));

        // Внешний идентификатор детерминирован ключом идемпотентности.
        return Task.FromResult(new PaymentResult(true, $"mock-{request.IdempotencyKey}", null));
    }
}
