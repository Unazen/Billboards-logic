namespace Billboard.Web.Services;

public sealed record DemoTariff(
    string Id,
    string Name,
    decimal MonthlyPrice,
    int MaxVideoCount,
    long MaxVideoSizeBytes,
    int MaxVideoDurationSeconds,
    string Description,
    IReadOnlyList<string> Features
);

public sealed record DemoPayment(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Type,
    decimal Amount,
    decimal BalanceAfter,
    string Description
);

public sealed class BillingDemoStore
{
    private readonly Dictionary<string, AccountBillingState> _accounts = new(StringComparer.Ordinal);

    private static readonly IReadOnlyList<DemoTariff> AvailableTariffs =
    [
        new("basic", "Старт", 0m, 5, 100L * 1024 * 1024, 15,
            "Для знакомства с платформой и первых размещений.",
            ["До 5 рекламных материалов", "Видео до 100 МБ и 15 секунд", "Базовая статистика"]),
        new("standard", "Стандарт", 750m, 25, 500L * 1024 * 1024, 30,
            "Для регулярных рекламных кампаний.",
            ["До 25 рекламных материалов", "Видео до 500 МБ и 30 секунд", "Расширенная статистика"]),
        new("business", "Бизнес", 1500m, 100, 2L * 1024 * 1024 * 1024, 60,
            "Для большого количества устройств и размещений.",
            ["До 100 рекламных материалов", "Видео до 2 ГБ и 60 секунд", "Приоритетная обработка"])
    ];

    public IReadOnlyList<DemoTariff> GetTariffs() => AvailableTariffs;

    public DemoTariff GetCurrentTariff(string accountKey)
    {
        var state = GetState(accountKey);
        return AvailableTariffs.First(tariff => tariff.Id == state.CurrentTariffId);
    }

    public decimal GetBalance(string accountKey) => GetState(accountKey).Balance;

    public IReadOnlyList<DemoPayment> GetPayments(string accountKey) =>
        GetState(accountKey).Payments
            .OrderByDescending(payment => payment.CreatedAt)
            .ToList();

    public bool TrySubscribe(string accountKey, string tariffId, out string message)
    {
        var tariff = AvailableTariffs.FirstOrDefault(item => item.Id == tariffId);
        if (tariff is null)
        {
            message = "Выбранный тариф недоступен.";
            return false;
        }

        var state = GetState(accountKey);
        if (state.CurrentTariffId == tariff.Id)
        {
            message = "Этот тариф уже подключён.";
            return false;
        }

        if (state.Balance < tariff.MonthlyPrice)
        {
            message = "Недостаточно средств. Пополните баланс и повторите попытку.";
            return false;
        }

        state.Balance -= tariff.MonthlyPrice;
        state.CurrentTariffId = tariff.Id;
        state.Payments.Add(new DemoPayment(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            "Оплата тарифа",
            -tariff.MonthlyPrice,
            state.Balance,
            $"Подключение тарифа «{tariff.Name}»"));

        message = $"Тариф «{tariff.Name}» подключён.";
        return true;
    }

    public bool TryTopUp(string accountKey, decimal amount, out string message)
    {
        if (amount < 100m || amount > 100_000m)
        {
            message = "Укажите сумму от 100 до 100 000 ₽.";
            return false;
        }

        var state = GetState(accountKey);
        state.Balance += amount;
        state.Payments.Add(new DemoPayment(
            Guid.NewGuid(),
            DateTimeOffset.Now,
            "Пополнение",
            amount,
            state.Balance,
            "Демо-пополнение баланса"));

        message = $"Баланс пополнен на {amount:N0} ₽.";
        return true;
    }

    private AccountBillingState GetState(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);

        if (_accounts.TryGetValue(accountKey, out var state))
            return state;

        state = new AccountBillingState
        {
            Payments =
            [
                new DemoPayment(Guid.NewGuid(), DateTimeOffset.Now.AddDays(-12), "Пополнение", 2_000m, 2_000m, "Демо-пополнение баланса"),
                new DemoPayment(Guid.NewGuid(), DateTimeOffset.Now.AddDays(-8), "Оплата размещения", -450m, 1_550m, "Размещение «Осенняя кампания»"),
                new DemoPayment(Guid.NewGuid(), DateTimeOffset.Now.AddDays(-3), "Оплата размещения", -300m, 1_250m, "Размещение «Реклама автомобиля»")
            ]
        };
        _accounts.Add(accountKey, state);
        return state;
    }

    private sealed class AccountBillingState
    {
        public decimal Balance { get; set; } = 1_250m;

        public string CurrentTariffId { get; set; } = "basic";

        public List<DemoPayment> Payments { get; init; } = [];
    }
}
