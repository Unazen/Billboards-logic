using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Billboard.UnitTests;

public class BillboardGroupTests
{
    private static BillboardGroupService Service(TestDb t) => new(t.New());

    [Fact]
    public async Task Create_and_list_group()
    {
        using var t = new TestDb();
        var first = await t.AddBillboardAsync("Билборд 1");
        var second = await t.AddBillboardAsync("Билборд 2");

        var created = await Service(t).CreateAsync(new BillboardGroupUpsertRequest("Центр"));
        Assert.Equal("Центр", created.Name);
        Assert.Empty(created.Billboards);

        var updated = await Service(t).AddBillboardsAsync(created.Id,
            new BillboardGroupMembersRequest(new[] { first, second }));

        Assert.Equal(2, updated.Billboards.Count);
        var list = await Service(t).ListAsync();
        Assert.Single(list);
        Assert.Equal(2, list[0].Billboards.Count);
    }

    [Fact]
    public async Task Group_name_must_be_unique()
    {
        using var t = new TestDb();
        await Service(t).CreateAsync(new BillboardGroupUpsertRequest("Центр"));

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Service(t).CreateAsync(new BillboardGroupUpsertRequest(" центр ")));

        Assert.Equal("GROUP_NAME_EXISTS", ex.Code);
    }

    [Fact]
    public async Task Repeated_billboard_add_is_ignored()
    {
        using var t = new TestDb();
        var billboard = await t.AddBillboardAsync("Билборд 1");
        var group = await Service(t).CreateAsync(new BillboardGroupUpsertRequest("Центр"));

        await Service(t).AddBillboardsAsync(group.Id, new BillboardGroupMembersRequest(new[] { billboard }));
        var updated = await Service(t).AddBillboardsAsync(group.Id,
            new BillboardGroupMembersRequest(new[] { billboard }));

        Assert.Single(updated.Billboards);
    }

    [Fact]
    public async Task Unknown_billboard_is_rejected()
    {
        using var t = new TestDb();
        var group = await Service(t).CreateAsync(new BillboardGroupUpsertRequest("Центр"));

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Service(t).AddBillboardsAsync(group.Id, new BillboardGroupMembersRequest(new[] { Guid.NewGuid() })));

        Assert.Equal("UNKNOWN_BILLBOARD", ex.Code);
    }

    [Fact]
    public async Task Remove_billboard_from_group()
    {
        using var t = new TestDb();
        var billboard = await t.AddBillboardAsync("Билборд 1");
        var group = await Service(t).CreateAsync(new BillboardGroupUpsertRequest("Центр"));
        await Service(t).AddBillboardsAsync(group.Id, new BillboardGroupMembersRequest(new[] { billboard }));

        var updated = await Service(t).RemoveBillboardAsync(group.Id, billboard);

        Assert.Empty(updated.Billboards);
    }

    [Fact]
    public async Task Delete_group_removes_members()
    {
        using var t = new TestDb();
        var billboard = await t.AddBillboardAsync("Билборд 1");
        var group = await Service(t).CreateAsync(new BillboardGroupUpsertRequest("Центр"));
        await Service(t).AddBillboardsAsync(group.Id, new BillboardGroupMembersRequest(new[] { billboard }));

        await Service(t).DeleteAsync(group.Id);

        await using var db = t.New();
        Assert.Empty(db.BillboardGroups);
        Assert.Empty(db.BillboardGroupMembers);
        Assert.Single(db.Billboards); // сам билборд не удаляется
    }

    [Fact]
    public async Task Missing_group_returns_not_found()
    {
        using var t = new TestDb();

        var ex = await Assert.ThrowsAsync<AppException>(() => Service(t).GetAsync(Guid.NewGuid()));

        Assert.Equal(404, ex.StatusCode);
    }
}
