using Billboard.Application.Common;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

/// <summary>
/// Единственное место, где меняются балансы. Методы не вызывают SaveChanges:
/// вызывающий сохраняет изменения кошелька, транзакции и связанных сущностей одной операцией.
/// </summary>
public sealed class WalletLedger(AppDbContext db)
{
    public async Task<Wallet> GetOrCreateWalletAsync(Guid userId, CancellationToken ct)
    {
        var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct);
        if (wallet is not null) return wallet;

        wallet = new Wallet { Id = Guid.NewGuid(), UserId = userId };
        db.Wallets.Add(wallet);
        return wallet;
    }

    public WalletTransaction TopUp(Wallet w, decimal amount, string key, Guid? related, string? description)
    {
        RequirePositive(amount);
        w.Balance += amount;
        return Record(w, WalletTransactionType.TopUp, amount, key, related, description);
    }

    /// <summary>Перемещает деньги из доступного баланса в резерв.</summary>
    public WalletTransaction Reserve(Wallet w, decimal amount, string key, Guid? related, string? description)
    {
        RequirePositive(amount);
        if (w.Balance < amount)
            throw AppException.Conflict("INSUFFICIENT_FUNDS",
                "Недостаточно средств на балансе для резервирования.",
                new { required = amount, available = w.Balance });

        w.Balance -= amount;
        w.ReservedBalance += amount;
        return Record(w, WalletTransactionType.Reservation, amount, key, related, description);
    }

    /// <summary>Списание из резерва за подтверждённый показ.</summary>
    public WalletTransaction ChargeFromReserve(Wallet w, decimal amount, string key, Guid? related, string? description)
    {
        RequirePositive(amount);
        if (w.ReservedBalance < amount)
            throw AppException.Conflict("INSUFFICIENT_RESERVE", "В резерве недостаточно средств.");

        w.ReservedBalance -= amount;
        return Record(w, WalletTransactionType.Charge, amount, key, related, description);
    }

    /// <summary>Прямое списание с доступного баланса (например, подписка).</summary>
    public WalletTransaction ChargeFromBalance(Wallet w, decimal amount, string key, Guid? related, string? description)
    {
        RequirePositive(amount);
        if (w.Balance < amount)
            throw AppException.Conflict("INSUFFICIENT_FUNDS", "Недостаточно средств на балансе.",
                new { required = amount, available = w.Balance });

        w.Balance -= amount;
        return Record(w, WalletTransactionType.Charge, amount, key, related, description);
    }

    /// <summary>Возвращает неиспользованный резерв на доступный баланс.</summary>
    public WalletTransaction Release(Wallet w, decimal amount, string key, Guid? related, string? description)
    {
        RequirePositive(amount);
        if (w.ReservedBalance < amount)
            throw AppException.Conflict("INSUFFICIENT_RESERVE", "В резерве недостаточно средств.");

        w.ReservedBalance -= amount;
        w.Balance += amount;
        return Record(w, WalletTransactionType.Release, amount, key, related, description);
    }

    public WalletTransaction Refund(Wallet w, decimal amount, string key, Guid? related, string? description)
    {
        RequirePositive(amount);
        w.Balance += amount;
        return Record(w, WalletTransactionType.Refund, amount, key, related, description);
    }

    private WalletTransaction Record(Wallet w, WalletTransactionType type, decimal amount, string key, Guid? related, string? description)
    {
        w.Version++;
        w.UpdatedAt = DateTimeOffset.UtcNow;

        var tx = new WalletTransaction
        {
            Id = Guid.NewGuid(),
            WalletId = w.Id,
            Type = type,
            Amount = amount,
            BalanceAfter = w.Balance,
            RelatedEntityId = related,
            IdempotencyKey = key,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.WalletTransactions.Add(tx);
        return tx;
    }

    private static void RequirePositive(decimal amount)
    {
        if (amount <= 0)
            throw AppException.Validation("INVALID_AMOUNT", "Сумма должна быть положительной.");
    }

    public static string UserKey(Guid userId, string key) => $"{userId:N}:{key}";
}

internal static class SystemLogWriter
{
    public static void Add(AppDbContext db, Guid? userId, string action, string? entityType, object? entityId,
        string result = "OK", string? details = null)
    {
        db.SystemLogs.Add(new SystemLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            Result = result,
            Details = details,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}
