using Billboard.Domain.Enums;

namespace Billboard.Domain.Entities;

public class TariffPlan
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int MaxVideoCount { get; set; }
    public long MaxVideoSizeBytes { get; set; }
    public int MaxVideoDurationSeconds { get; set; }
    public CompressionLevel CompressionLevel { get; set; }
    public bool AdvancedStatistics { get; set; }
    public bool PriorityProcessing { get; set; }

    /// <summary>Скидка при размещении сразу на нескольких билбордах, %.</summary>
    public decimal MultiBillboardDiscountPercent { get; set; }

    /// <summary>Стоимость подписки за 30 дней (виртуальные деньги).</summary>
    public decimal MonthlyPrice { get; set; }

    /// <summary>Тариф, действующий без подписки.</summary>
    public bool IsDefault { get; set; }
    public string? FeaturesJson { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TariffPlanId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public TariffPlan? TariffPlan { get; set; }
}

public class Wallet
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Доступные средства.</summary>
    public decimal Balance { get; set; }

    /// <summary>Средства, зарезервированные под размещения.</summary>
    public decimal ReservedBalance { get; set; }
    public string Currency { get; set; } = "USD";

    /// <summary>Токен оптимистичной блокировки: защищает от гонок при списаниях.</summary>
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class WalletTransaction
{
    public Guid Id { get; set; }
    public Guid WalletId { get; set; }
    public WalletTransactionType Type { get; set; }

    /// <summary>Всегда положительная сумма; смысл задаёт Type.</summary>
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public Guid? RelatedEntityId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class BudgetReservation
{
    public Guid Id { get; set; }
    public Guid PlacementId { get; set; }
    public Guid WalletId { get; set; }
    public decimal ReservedAmount { get; set; }
    public decimal ChargedAmount { get; set; }
    public decimal ReleasedAmount { get; set; }

    /// <summary>Цена одного показа с учётом скидки (снимок на момент резервирования).</summary>
    public decimal PricePerPlay { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public decimal Remaining => ReservedAmount - ChargedAmount - ReleasedAmount;
}

/// <summary>Начисление владельцу билбордов за подтверждённый показ.</summary>
public class OwnerEarning
{
    public Guid Id { get; set; }
    public Guid BillboardId { get; set; }
    public Guid PlacementId { get; set; }
    public Guid PlaybackEventId { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SystemLog
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string Result { get; set; } = "OK";
    public string? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
