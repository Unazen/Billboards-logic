using Billboard.Domain.Enums;

namespace Billboard.Domain.Entities;

// ВНИМАНИЕ: класс назван BillboardDevice, а не Billboard: имя Billboard конфликтует
// с корневым пространством имён Billboard.* и ломает компиляцию в соседних проектах.
public class BillboardDevice
{
    public Guid Id { get; set; }

    /// <summary>Владелец устройства (клиент/партнёр). null — устройство компании.</summary>
    public Guid? OwnerId { get; set; }

    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    /// <summary>Лимит локального кэша, по умолчанию 32 ГБ.</summary>
    public long CapacityBytes { get; set; } = 32L * 1024 * 1024 * 1024;

    /// <summary>Сколько секунд в час можно отдать под рекламу.</summary>
    public int AdvertCapacitySecondsPerHour { get; set; } = 1800;

    public BillboardStatus Status { get; set; } = BillboardStatus.Offline;
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Условная цена одного подтверждённого показа.</summary>
    public decimal PricePerPlay { get; set; } = 0.05m;

    /// <summary>IANA или Windows id часового пояса билборда.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>SHA-256 токена устройства (сам токен не хранится).</summary>
    public string? DeviceTokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class BillboardGroup
{
    public Guid Id { get; set; }

    /// <summary>Владелец группы. null — общая группа компании.</summary>
    public Guid? OwnerId { get; set; }

    public string Name { get; set; } = "";
    public List<BillboardGroupMember> Members { get; set; } = new();
}

public class BillboardGroupMember
{
    public Guid BillboardGroupId { get; set; }
    public Guid BillboardId { get; set; }
    public BillboardGroup? Group { get; set; }
    public BillboardDevice? Billboard { get; set; }
}

public class DeviceHeartbeat
{
    public Guid Id { get; set; }
    public Guid BillboardId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? SimulatorVersion { get; set; }
    public BillboardStatus Status { get; set; }
}

public class DeviceCachedVideo
{
    public Guid Id { get; set; }
    public Guid BillboardId { get; set; }
    public Guid VideoAssetId { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public string DownloadStatus { get; set; } = "Pending";
    public bool IntegrityVerified { get; set; }
}
