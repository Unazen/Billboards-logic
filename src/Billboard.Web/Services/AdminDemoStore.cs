namespace Billboard.Web.Services;

public record AdminUser(int Id, string Name, string Email);

public record BillboardDevice(
	int Id,
	string Name,
	int OwnerId,
	string Location,
	bool IsOnline
);

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

	private int _nextUserId = 4;
	private int _nextDeviceId = 5;

	public IReadOnlyList<AdminUser> Users => _users;
	public IReadOnlyList<BillboardDevice> Devices => _devices;

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
		NotifyChanged();
	}

	private void NotifyChanged()
	{
		Changed?.Invoke();
	}
}
