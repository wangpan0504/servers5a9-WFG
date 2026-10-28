using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpTotalRosterTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-total-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(10, 4, 10, 10);
            var map = new TownMap("total-roster-fixture.map", [new(new(0, 0, 100, 100), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(10, 4)] = new(10, 4, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var memory = new GameCache();
            var owners = new AccountOwners(memory);
            using var owner = owners.Acquire("total-roster", () => { });
            var first = repository.Create(owner, new("TotalFirst", point, point));
            var hole = repository.Create(owner, new("TotalHole", point, point));
            var selected = repository.Create(owner, new("TotalPick", point, point));
            var third = repository.Create(owner, new("TotalThird", point, point));
            var last = repository.Create(owner, new("TotalLast", point, point));
            repository.Delete(owner, repository.LoadDeletionTarget(owner, hole.Slot));
            var session = new GameSession(repository, towns, owner, entry: new(new(1, 47), 8, "[pvp]"));
            That(session.CapturePvpTotalRoster([3, 1, 0]) is null, "未发布或未选角不能解释总力角色索引");
            session.Select(first.Slot); session.MarkReady();
            That(session.CapturePvpTotalRoster([3, 1, 0]) is null, "直接选角不能伪造客户端总力名册");
            session.ReturnToRoster();
            var published = session.PrepareRoster();
            session.MarkRosterPublished(published);
            session.SelectFromRosterIndex(1);
            That(session.CapturePvpTotalRoster([3, 1, 0]) is null, "入城未就绪不提供总力阵容");
            session.MarkReady();
            var original = session.Selected!;
            var selection = session.CapturePvpTotalRoster([3, 1, 0]) ?? throw new InvalidOperationException("Total roster fixture was not admitted");
            That(selection.Members.Select(row => row.Character.Id).SequenceEqual(new[] { last.Id, selected.Id, first.Id }) &&
                selection.Members.Select(row => row.Character.Slot).SequenceEqual(new ushort[] { 4, 2, 0 }) &&
                selection.Members.Select(row => row.RosterIndex).SequenceEqual(new byte[] { 3, 1, 0 }),
                "三角色按请求顺序映射原NOTI2行，有空洞时索引1绑定槽2而非槽1");
            That(session.CanUsePvpTotalRoster(selection) && ReferenceEquals(selection.Roster, session.PublishedTagEvildomRoster()),
                "总力复用选角成功绑定的唯一名册，TAG仍读取同一映射");
            That(session.CapturePvpTotalRoster([1, 1, 0]) is null && session.CapturePvpTotalRoster([0, 1]) is null &&
                session.CapturePvpTotalRoster([0, 1, 99]) is null && session.CapturePvpTotalRoster(default) is null,
                "总力必须三个不同的有效发布索引，拒绝缺项、重复、越界及默认数组");
            That(session.CapturePvpTotalRoster([3, 1, 0], blocked: true) is null, "外部活动门禁不产生总力阵容");
            var wrong = new GameSession(repository, towns, owner, entry: new(new(1, 40), 22, "[normal]"));
            wrong.MarkRosterPublished(wrong.PrepareRoster()); wrong.SelectFromRosterIndex(1); wrong.MarkReady();
            That(wrong.CapturePvpTotalRoster([3, 1, 0]) is null && !wrong.CanUsePvpTotalRoster(selection),
                "普通频道及同账号另一Session不能借用总力阵容");

            var added = repository.Create(owner, new("TotalAdded", point, point));
            var afterAdd = session.CapturePvpTotalRoster([3, 1, 0]);
            That(added.Slot == 1 && afterAdd is not null && afterAdd.Members[1].Character.Id == selected.Id &&
                session.CanUsePvpTotalRoster(selection), "当前名册插入空槽后仍按已发布顺序解释，不重排请求索引");
            repository.SwapSlots(owner, first.Slot, last.Slot);
            That(session.CapturePvpTotalRoster([3, 1, 0]) is null && !session.CanUsePvpTotalRoster(selection),
                "成员槽位变化使已发布阵容失效，不能静默绑定换位后的角色");
            repository.SwapSlots(owner, first.Slot, last.Slot);
            repository.Delete(owner, repository.LoadDeletionTarget(owner, first.Slot));
            var replacementCharacter = repository.Create(owner, new("TotalNew", point, point));
            That(replacementCharacter.Slot == first.Slot && replacementCharacter.Id != first.Id &&
                session.CapturePvpTotalRoster([3, 1, 0]) is null && !session.CanUsePvpTotalRoster(selection),
                "删角后同槽新角色不能冒充冻结ID");
            var persisted = repository.LoadCharacter(owner, selected.Slot);
            That(persisted.Revision == original.Revision && persisted.Position == original.Position &&
                persisted.Inventory.Gold == original.Inventory.Gold, "阵容映射与校验不写当前角色资产、位置或版本");
            session.ReturnToRoster();
            That(!session.CanUsePvpTotalRoster(selection), "返回选角立即使旧阵容失效");
            session.MarkRosterPublished(published);
            Reject<InvalidDataException>(() => session.SelectFromRosterIndex(1), "旧发布名册不能跨选角epoch重建总力阵容");
            session.MarkRosterPublished(session.PrepareRoster()); session.SelectFromRosterIndex(2); session.MarkReady();
            var next = session.CapturePvpTotalRoster([4, 2, 1])!;
            That(next.Actor.Epoch != selection.Actor.Epoch && !session.CanUsePvpTotalRoster(selection) &&
                session.CanUsePvpTotalRoster(next), "重新发布名册绑定新epoch，原阵容不能回放到新选角");
            using var replacementOwner = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => session.CanUsePvpTotalRoster(next), "旧owner不能核准接管者的总力阵容");
            var replacement = new GameSession(repository, towns, replacementOwner, entry: new(new(1, 40), 24, "[pvp]"));
            replacement.MarkRosterPublished(replacement.PrepareRoster()); replacement.SelectFromRosterIndex(2); replacement.MarkReady();
            var fresh = replacement.CapturePvpTotalRoster([4, 2, 1])!;
            session.End();
            That(!replacement.CanUsePvpTotalRoster(next) && replacement.CanUsePvpTotalRoster(fresh),
                "旧连接迟到收尾不影响新Session实际发布的名册及阵容");
            replacement.End();
            That(!replacement.CanUsePvpTotalRoster(fresh), "断线清除当前发布名册绑定");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} 总力发布名册、三角色索引与生命周期断言");
    }
}
