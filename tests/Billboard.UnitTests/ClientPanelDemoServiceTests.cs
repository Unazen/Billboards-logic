using Billboard.Web.Services;

namespace Billboard.UnitTests;

public sealed class ClientPanelDemoServiceTests
{
    [Fact]
    public void DemoAccountSelectionFiltersClientDataByOwner()
    {
        var adminStore = new AdminDemoStore();
        var scheduleStore = new ScheduleDemoStore();
        var session = new DemoUserSession(adminStore);
        var panel = new ClientPanelDemoService(adminStore, scheduleStore, session);

        Assert.True(session.TrySignIn("user-2"));
        Assert.Equal(2, panel.UserId);
        Assert.Equal("Анна Смирнова", panel.UserName);
        Assert.All(panel.GetBillboards(), device => Assert.Equal(2, device.OwnerId));
        Assert.All(panel.GetVideos(), video => Assert.Equal(2, video.OwnerId));
        Assert.Contains(panel.GetVideos(), video => video.Id == 2);
        Assert.Equal(2, panel.GetMedia().Count);
        Assert.All(panel.GetMedia(), media => Assert.Equal(2, media.OwnerId));

        panel.RemoveMedia(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        Assert.Equal(2, panel.GetMedia().Count);
    }

    [Fact]
    public void DemoAccountSelectionSupportsAdminAndRejectsUnknownAccount()
    {
        var session = new DemoUserSession(new AdminDemoStore());

        Assert.False(session.HasSelectedAccount);
        Assert.Equal("Выберите пользователя", session.CurrentName);

        Assert.True(session.TrySignIn("admin"));
        Assert.True(session.HasSelectedAccount);
        Assert.True(session.IsAdministrator);
        Assert.Equal("Администратор", session.CurrentName);

        Assert.False(session.TrySignIn("user-999"));
        Assert.True(session.IsAdministrator);
    }

    [Fact]
    public void ClientCannotToggleOrRemoveAnotherOwnersSchedule()
    {
        var scheduleStore = new ScheduleDemoStore();

        scheduleStore.ToggleActive(1, ownerId: 2);
        scheduleStore.RemoveSchedule(1, ownerId: 2);

        var schedule = Assert.Single(scheduleStore.Schedules, item => item.Id == 1);
        Assert.True(schedule.IsActive);
        Assert.Equal(1, schedule.OwnerId);
    }
}
