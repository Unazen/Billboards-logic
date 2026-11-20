using Billboard.Api.Infrastructure;
using Billboard.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Billboard.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/billing")]
public class BillingController(IWalletService wallets, IBillingService billing) : ApiControllerBase
{
    [HttpGet("wallet")]
    public async Task<ActionResult<WalletDto>> GetWallet(CancellationToken ct) =>
        Ok(await wallets.GetWalletAsync(UserId, ct));

    /// <summary>Имитация пополнения. Повтор с тем же IdempotencyKey не пополняет баланс второй раз.</summary>
    [HttpPost("top-ups")]
    public async Task<ActionResult<WalletTransactionDto>> TopUp([FromBody] TopUpRequest request, CancellationToken ct) =>
        Ok(await wallets.TopUpAsync(UserId, request, ct));

    [HttpGet("transactions")]
    public async Task<ActionResult<IReadOnlyList<WalletTransactionDto>>> Transactions(
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await wallets.GetTransactionsAsync(UserId, skip, take, ct));

    /// <summary>Предварительный расчёт стоимости. billboardCount > 1 включает скидку тарифа.</summary>
    [HttpGet("quote/{placementId:guid}")]
    public async Task<ActionResult<QuoteDto>> Quote(Guid placementId, [FromQuery] int billboardCount = 1, CancellationToken ct = default) =>
        Ok(await billing.QuoteAsync(UserId, IsAdmin, placementId, Math.Max(1, billboardCount), ct));

    [HttpPost("subscriptions")]
    public async Task<ActionResult<SubscriptionDto>> Subscribe([FromBody] SubscribeRequest request, CancellationToken ct) =>
        Ok(await billing.SubscribeAsync(UserId, request, ct));

    [HttpPost("subscriptions/{id:guid}/cancel")]
    public async Task<IActionResult> CancelSubscription(Guid id, CancellationToken ct)
    {
        await billing.CancelSubscriptionAsync(UserId, id, ct);
        return NoContent();
    }

    /// <summary>Доход владельца билбордов по подтверждённым показам (только администратор).</summary>
    [HttpGet("earnings")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<EarningsSummaryDto>> Earnings(
        [FromQuery] Guid? billboardId, [FromQuery] int take = 100, CancellationToken ct = default) =>
        Ok(await billing.GetEarningsAsync(billboardId, take, ct));

    /// <summary>Показы и затраты по размещениям: клиенту — свои, администратору — все (?all=true).</summary>
    [HttpGet("usage")]
    public async Task<ActionResult<IReadOnlyList<PlacementUsageDto>>> Usage([FromQuery] bool all, CancellationToken ct) =>
        Ok(await billing.GetUsageAsync(all && IsAdmin ? null : UserId, ct));
}
