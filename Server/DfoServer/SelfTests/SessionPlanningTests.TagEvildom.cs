using DfoServer.Game.Session;
using DfoServer.Game.TagEvildom;
using System.Collections.Immutable;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    static void TagEvildomConfigurationLifecycle()
    {
        using var fixture = new Fixture();
        fixture.Sql("UPDATE characters SET level=95");
        var first = fixture.Read();
        var second = fixture.Repository.Create(fixture.Owner, new("TagPartner", fixture.Towns.Room, fixture.Towns.InitialReturn)
            { Level = 95, InitialLearning = first.Learning });
        var session = new GameSession(fixture.Repository, fixture.Towns, fixture.Owner,
            entry: new(new(2, 46), 53, "[evildom_gathering]"));
        session.MarkRosterPublished(session.PrepareRoster());
        session.SelectFromRosterIndex(0); session.MarkReady();
        var now = DateTimeOffset.Parse("2026-10-26T01:00:00Z");
        var period = TagEvildomPeriod.At(now);
        ImmutableArray<TagEvildomMemberRequest> members = [new(2, 0, 0), new(2, 1, 0), new(0, -1, 0), new(0, -1, 0), new(0, -1, 0), new(0, -1, 0)];
        string characters = fixture.Rows("SELECT * FROM characters ORDER BY id");
        var saved = session.ConfigureTagEvildom(fixture.Repository, members, 0, now);
        That(saved.Projection is { } result && result.State.Configuration.Teams[0] == new TagEvildomTeam(first.Id, second.Id) &&
            session.CanPublishTagEvildom(result) && session.TagEvildomStandby(result)?.Id == second.Id,
            "真实已发名册配置映射stableId，当前队首和备用完整角色来自同owner");
        foreach (var invalid in new[]
        {
            members.SetItem(1, new(1, 1, 0)), members.SetItem(1, new(2, int.MaxValue, 0)),
            members.SetItem(1, new(2, 1, 1)), members.SetItem(1, new(2, 0, 0)),
            members.SetItem(1, new(0, -1, 0))
        }) That(session.ConfigureTagEvildom(fixture.Repository, invalid, 0, now).Projection is null,
            "跨区/越界/未支持aux/重复角色/半队拒绝且不返回成功回执");
        That(session.ConfigureTagEvildom(fixture.Repository, members, 3, now).Projection is null &&
            fixture.Rows("SELECT * FROM characters ORDER BY id") == characters,
            "非法选择和编队操作不动角色完整行");
        var captured = session.CaptureTagEvildomCharacters(saved.Projection!)!;
        That(session.CanPublishTagEvildomCharacters(fixture.Repository, captured), "捕获实际主备快照后允许当前资料投影");
        var changedBackup = session.UpdateProgress(new(Game.Characters.CharacterProgressField.Skill, "planning", second.Name, 5, 2));
        That(changedBackup.Revision == captured.Standby.Revision && !session.CanPublishTagEvildomCharacters(fixture.Repository, captured),
            "备用GM同revision更新也使等待中的旧1394回执失效，不仅比较数据库revision");
        var updatedCapture = session.CaptureTagEvildomCharacters(saved.Projection!)!;
        session.UpdateProgress(new(Game.Characters.CharacterProgressField.Story, "planning", first.Name, 10, 0));
        That(!session.CanPublishTagEvildomCharacters(fixture.Repository, updatedCapture), "主角同epoch更新使旧双角色资料失效");
        fixture.Sql("INSERT INTO account_wallets(account,revision,premium_contracts) VALUES('planning',1,'{\"27\":2000000000}') " +
            "ON CONFLICT(account) DO UPDATE SET revision=revision+1,premium_contracts=excluded.premium_contracts");
        // 模拟正常账号权益刷新后的主角快照；仅改DB而不刷新本人仍应被guard拒绝。
        session.UpdateProgress(new(Game.Characters.CharacterProgressField.Story, "planning", first.Name, 10, 0));
        var purchasedContract = session.CaptureTagEvildomCharacters(saved.Projection!)!;
        That(purchasedContract is not null && purchasedContract.Standby.Learning.ExpertContractExpiresAt == 2000000000 &&
            session.CanPublishTagEvildomCharacters(fixture.Repository, purchasedContract),
            "仅商城27契约存在也能合法捕获和投影，采用max(legacy,purchased)有效期限口径");
        fixture.Sql("UPDATE account_wallets SET revision=revision+1,premium_contracts='{\"27\":2000000000,\"56\":2000000100}' WHERE account='planning'");
        That(!session.CanPublishTagEvildomCharacters(fixture.Repository, purchasedContract!), "单改账号56权益且角色revision不变，也使旧虚弱属性资料失效");
        fixture.Sql($"INSERT INTO account_tag_evildom_clearances VALUES('trusted-reward','planning',{period.Day},{period.Week},{first.Id},{second.Id})");
        var protectedState = fixture.Repository.ReadTagEvildomState(fixture.Owner, period);
        That(protectedState.UsedThisWeek == 1 && protectedState.ProtectedMembers.SetEquals(new[] { first.Id, second.Id }),
            "已有正式领奖事实读成真实周次数与当天角色保护，不能拿请求代替");
        var reset = session.ConfigureTagEvildom(fixture.Repository, null, 0, now);
        That(reset.Projection!.State.Configuration.SameAssignments(saved.Projection!.State.Configuration) && reset.Projection.State.UsedThisWeek == 1,
            "初始化保留当天已领奖队伍及周次数，重复配置不推进revision");
        var empty = members.Select(_ => new TagEvildomMemberRequest(0, -1, 0)).ToImmutableArray();
        That(session.ConfigureTagEvildom(fixture.Repository, empty, 0, now).Projection is null,
            "不能用全空设置绕过当天重置保护");
        var reopened = new DfoServer.Persistence.CharacterRepository(fixture.Database).ReadTagEvildomState(fixture.Owner, period);
        That(reopened.UsedThisWeek == 1 && reopened.ProtectedMembers.SetEquals(protectedState.ProtectedMembers), "重开库不清领奖事实");
        var tomorrow = session.ConfigureTagEvildom(fixture.Repository, null, 0, now.AddDays(1));
        That(tomorrow.Projection!.State.Configuration.Teams.All(team => team.Empty) && tomorrow.Projection.State.UsedThisWeek == 1,
            "过日允许重置但同周次数保留，配置请求不创建新通关");
        That(fixture.Scalar("SELECT count(*) FROM account_tag_evildom_clearances") is 1L, "设置/初始化只读通关账本");
        session.ReturnToRoster();
        That(!session.CanPublishTagEvildom(saved.Projection!) && session.ConfigureTagEvildom(fixture.Repository, members, 0, now).Projection is null,
            "小退后的旧回执及配置请求不能跨epoch发布");
        session.End();
        That(TagEvildomPeriod.At(DateTimeOffset.Parse("2026-10-25T21:59:59Z")) == new TagEvildomPeriod(20261025, 20261019) &&
            TagEvildomPeriod.At(DateTimeOffset.Parse("2026-10-25T22:00:00Z")) == new TagEvildomPeriod(20261026, 20261026),
            "再战周一UTC+8六点边界，不误用周四或UTC零点");
    }

    static void TagEvildomPublishedRoster()
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        That(session.PublishedTagEvildomRoster() is null, "未发布、未选角不能生成TAG候选索引");
        fixture.Select();
        That(session.PublishedTagEvildomRoster() is null, "内部直接选角不伪造已发送的原生名册");
        session.ReturnToRoster();
        var removed = fixture.Repository.Create(fixture.Owner, new("TagRemoved", fixture.Towns.Room, fixture.Towns.InitialReturn));
        var last = fixture.Repository.Create(fixture.Owner, new("TagLast", fixture.Towns.Room, fixture.Towns.InitialReturn) { Level = 95 });
        fixture.Repository.Delete(fixture.Owner, fixture.Repository.LoadDeletionTarget(fixture.Owner, removed.Slot));
        var published = session.PrepareRoster();
        session.MarkRosterPublished(published);
        session.SelectFromRosterIndex(1);
        That(session.PublishedTagEvildomRoster() is null, "入城初始化未完成时不发布TAG映射");
        session.MarkReady();
        var tag = session.PublishedTagEvildomRoster()!;
        That(tag.Generation == published.Generation && tag.SelectionEpoch == session.SelectionEpoch &&
            tag.SelectionEpoch != published.SelectionEpoch && tag.Entries.SequenceEqual(published.Entries) &&
            tag.Entries[1].Id == last.Id && tag.Entries[1].Slot == last.Slot && last.Slot == 2,
            "成功入城独立绑定原NOTI2顺序，不把有空洞的索引1解释为槽位1或过滤后索引");
        session.MarkRosterPublished(published);
        That(ReferenceEquals(session.PublishedTagEvildomRoster(), tag), "迟到旧名册安装不能替换当前TAG绑定");
        session.ReturnToRoster();
        That(session.PublishedTagEvildomRoster() is null, "退出清除TAG候选映射");
        session.MarkRosterPublished(published);
        Reject<InvalidDataException>(() => session.SelectFromRosterIndex(1), "小退后旧名册不能重建TAG生命周期");
        session.MarkRosterPublished(session.PrepareRoster());
        session.SelectFromRosterIndex(1);
        session.MarkReady();
        That(session.PublishedTagEvildomRoster()!.SelectionEpoch != tag.SelectionEpoch, "重选有新的绑定epoch");
        session.End();
        That(session.PublishedTagEvildomRoster() is null, "断线清除当前会话TAG映射");

        using var replacement = fixture.Owners.Acquire(fixture.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => session.PublishedTagEvildomRoster(), "旧generation不能取得替代连接的TAG候选");
        var reconnected = new GameSession(fixture.Repository, fixture.Towns, replacement);
        That(reconnected.PublishedTagEvildomRoster() is null, "重登不继承另一连接发布的索引");
        session.End();
        reconnected.MarkRosterPublished(reconnected.PrepareRoster());
        reconnected.SelectFromRosterIndex(1);
        reconnected.MarkReady();
        That(reconnected.PublishedTagEvildomRoster()!.Entries[1].Id == last.Id, "旧连接迟到清理不影响新owner实际发布的名册");
        reconnected.End();
    }
}
