using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Domain.Entities;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

public sealed class WalletService(AppDbContext db, WalletLedger ledger, IPaymentGateway payments) : IWalletService
{
    public async Task<WalletDto> GetWalletAsync(Guid userId, CancellationToken ct = default)
    {
        var wallet = await ledger.GetOrCreateWalletAsync(userId, ct);
        await db.SaveChangesAsync(ct);
        return Map(wallet);
    }

    public async Task<WalletTransactionDto> TopUpAsync(Guid userId, TopUpRequest request, CancellationToken ct = default)
    {
        var key = WalletLedger.UserKey(userId, $"topup:{request.IdempotencyKey}");

        // Повторный запрос с тем же ключом возвращает прежний результат без нового начисления.
        var existing = await db.WalletTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.IdempotencyKey == key, ct);
        if (existing is not null) return Map(existing);

        var payment = await payments.ChargeAsync(
            new PaymentRequest(userId, request.Amount, key, request.SimulateFailure), ct);

        if (!payment.Success)
        {
            SystemLogWriter.Add(db, userId, "TopUpFailed", nameof(Wallet), null, "FAILED", payment.Error);
            await db.SaveChangesAsync(ct);
            throw new AppException("PAYMENT_FAILED", payment.Error ?? "Платёж отклонён.", 402);
        }

        var wallet = await ledger.GetOrCreateWalletAsync(userId, ct);
        var tx = ledger.TopUp(wallet, request.Amount, key, null, $"Пополнение ({payment.ExternalId})");
        SystemLogWriter.Add(db, userId, "TopUp", nameof(Wallet), wallet.Id, "OK", request.Amount.ToString("0.####"));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENT_UPDATE", "Баланс изменился одновременно. Повторите запрос.");
        }
        catch (DbUpdateException)
        {
            // Параллельный запрос с тем же ключом успел сохраниться первым.
            db.ChangeTracker.Clear();
            var duplicate = await db.WalletTransactions.AsNoTracking()
                .FirstOrDefaultAsync(t => t.IdempotencyKey == key, ct);
            if (duplicate is not null) return Map(duplicate);
            throw;
        }

        return Map(tx);
    }

    public async Task<IReadOnlyList<WalletTransactionDto>> GetTransactionsAsync(Guid userId, int skip, int take, CancellationToken ct = default)
    {
        var wallet = await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == userId, ct);
        if (wallet is null) return Array.Empty<WalletTransactionDto>();

        take = Math.Clamp(take, 1, 200);
        var items = await db.WalletTransactions.AsNoTracking()
            .Where(t => t.WalletId == wallet.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(Math.Max(0, skip)).Take(take)
            .ToListAsync(ct);
        return items.Select(Map).ToList();
    }

    public static WalletDto Map(Wallet w) => new(w.Id, w.Balance, w.ReservedBalance, w.Currency);

    public static WalletTransactionDto Map(WalletTransaction t) =>
        new(t.Id, t.Type.ToString(), t.Amount, t.BalanceAfter, t.RelatedEntityId, t.Description, t.CreatedAt);
}
