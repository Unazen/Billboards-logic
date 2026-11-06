namespace Billboard.Domain.Enums;

public enum BillboardStatus { Online, Offline, Error, Disabled }

public enum VideoStatus { Uploading, Processing, Ready, Failed }

public enum PlacementStatus
{
    Draft,
    PendingPayment,
    Scheduled,
    Downloading,
    Ready,
    Active,
    Paused,
    Completed,
    Cancelled,
    Failed
}

public enum SubscriptionStatus { Active, Expired, Cancelled }

public enum WalletTransactionType { TopUp, Reservation, Charge, Release, Refund, OwnerCredit }

public enum PlaybackResult { Completed, Skipped, Error }

public enum UploadJobStatus { Pending, Running, Succeeded, Failed }

/// <summary>Битовая маска дней недели для AdPlacement.DaysOfWeekMask.</summary>
[Flags]
public enum WeekDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    All = Weekdays | Saturday | Sunday
}

/// <summary>Уровень сжатия видео, определяется тарифом.</summary>
public enum CompressionLevel { None = 0, Basic = 1, Full = 2 }
