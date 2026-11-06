
namespace Billboard.Web.Services;

public record AdminUser(int Id, string Name, string Email);

public record BillboardDevice(
    int Id,
    string Name,
    int OwnerId,
    string Location,
    bool IsOnline
);

public record BillboardGroup(
    int Id,
    string Name,
    string Description,
    int OwnerId
);

public record BillboardGroupDevice(int GroupId, int DeviceId);

public class AdminDemoStore
{
    private readonly List<AdminUser> _users =
    [
        new(1, "Иван Петров", "ivan@example.com"),
        new(2, "Анна Смирнова", "anna@example.com"),
        new(3, "Алексей Иванов", "alex@example.com")
    ];

    private readonly List<BillboardDevice> _devices =
    [
        new(1, "Билборд №1", 1, "Москва, ул. Ленина", true),
        new(2, "Билборд №2", 1, "Москва, ул. Мира", true),
        new(3, "Билборд №3", 2, "Санкт-Петербург", false),
        new(4, "Билборд №4", 3, "Казань", true)
    ];

    private readonly List<BillboardGroup> _groups = [];
    private readonly List<BillboardGroupDevice> _groupDevices = [];

    private int _nextUserId = 4;
    private int _nextDeviceId = 5;
    private int _nextGroupId = 1;

    public IReadOnlyList<AdminUser> Users => _users;
    public IReadOnlyList<BillboardDevice> Devices => _devices;
    public IReadOnlyList<BillboardGroup> Groups => _groups;

    // Пока файловое хранилище не реализовано.
    public int UploadedFilesCount { get; private set; }

    public event Action? Changed;

    public void AddUser(string name, string email)
    {
        _users.Add(new AdminUser(_nextUserId++, name, email));
        NotifyChanged();
    }

    public bool RemoveUser(int id)
    {
        // Нельзя удалить пользователя, пока у него есть устройства.
        if (_devices.Any(device => device.OwnerId == id))
            return false;

        var user = _users.FirstOrDefault(user => user.Id == id);

        if (user is null)
            return false;

        var userGroups = _groups
            .Where(group => group.OwnerId == id)
            .ToList();

        foreach (var group in userGroups)
            RemoveGroupInternal(group.Id);

        _users.Remove(user);
        NotifyChanged();
        return true;
    }

    public void AddDevice(
        string name,
        int ownerId,
        string location)
    {
        _devices.Add(new BillboardDevice(
            _nextDeviceId++,
            name,
            ownerId,
            location,
            true
        ));

        NotifyChanged();
    }

    public void RemoveDevice(int id)
    {
        var device = _devices.FirstOrDefault(d => d.Id == id);

        if (device is null)
            return;

        _devices.Remove(device);

        // Удаляем устройство из всех групп.
        _groupDevices.RemoveAll(item => item.DeviceId == id);

        NotifyChanged();
    }

    // -----------------------------
    // Управление группами
    // -----------------------------

    public bool AddGroup(string name, string description, int ownerId)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!_users.Any(user => user.Id == ownerId))
            return false;

        _groups.Add(new BillboardGroup(
            _nextGroupId++,
            name.Trim(),
            description.Trim(),
            ownerId
        ));

        NotifyChanged();
        return true;
    }

    public bool RemoveGroup(int groupId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);

        if (group is null)
            return false;

        RemoveGroupInternal(groupId);
        NotifyChanged();
        return true;
    }

    private void RemoveGroupInternal(int groupId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);

        if (group is null)
            return;

        _groups.Remove(group);
        _groupDevices.RemoveAll(item => item.GroupId == groupId);
    }

    public IReadOnlyList<BillboardDevice> GetDevicesForGroup(int groupId)
    {
        var deviceIds = _groupDevices
            .Where(item => item.GroupId == groupId)
            .Select(item => item.DeviceId)
            .ToHashSet();

        return _devices
            .Where(device => deviceIds.Contains(device.Id))
            .ToList();
    }

    public IReadOnlyList<BillboardDevice> GetAvailableDevicesForGroup(int groupId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);

        if (group is null)
            return [];

        var alreadyAddedIds = _groupDevices
            .Where(item => item.GroupId == groupId)
            .Select(item => item.DeviceId)
            .ToHashSet();

        return _devices
            .Where(device =>
                device.OwnerId == group.OwnerId &&
                !alreadyAddedIds.Contains(device.Id))
            .ToList();
    }

    public bool AddDeviceToGroup(int groupId, int deviceId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);
        var device = _devices.FirstOrDefault(d => d.Id == deviceId);

        if (group is null || device is null)
            return false;

        // В группу можно добавлять только устройства её владельца.
        if (group.OwnerId != device.OwnerId)
            return false;

        // Не допускаем повторного добавления.
        if (_groupDevices.Any(item =>
            item.GroupId == groupId && item.DeviceId == deviceId))
            return false;

        _groupDevices.Add(new BillboardGroupDevice(groupId, deviceId));

        NotifyChanged();
        return true;
    }

    public bool RemoveDeviceFromGroup(int groupId, int deviceId)
    {
        var membership = _groupDevices.FirstOrDefault(item =>
            item.GroupId == groupId && item.DeviceId == deviceId);

        if (membership is null)
            return false;

        _groupDevices.Remove(membership);

        NotifyChanged();
        return true;
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
