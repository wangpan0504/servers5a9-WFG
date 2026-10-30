using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MarriagePersistenceTests
{
    static void InventoryDeadlines()
    {
        var clock = new ConsumableUseSessionTests.FakeClock();
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var template = new ItemTemplate(10002610, ItemKind.Material, 0, 0, 0, 999, 0, 0, 1,
            "[trade]", ushort.MaxValue, 0, "材料");
        items.PrefillTemplates([new(template.Id, template),
            new(10002611, template with { Id = 10002611, Expiration = new(AbsoluteTime: now + 5) })]);
        var gold = new GoldLimitCatalog("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        var pieces = new EpicPieceCatalog("[piece list]\n999998 999999\n[/piece list]");
        var planner = new MarriageInventoryPlanner(items, gold, pieces, clock);
        var slot = new InventorySlot(InventoryListType.Main, 121);
        var role = new Character(1, 0, "Expiry", 0, new(1, 0, 50, 50), null)
        {
            Inventory = new([new(1, slot, new(template.Id, ItemKind.Material, 2))], Gold: 1000)
        };
        foreach (ushort? source in new ushort?[] { 121, null })
        {
            InventoryResult Plan(Character value) => planner.Plan(value, [new(value.Id, template.Id, -1, source)], clock.GetUtcNow()) is { } plan
                ? new(true, 0, plan) : InventoryResult.Reject();
            InventoryQualificationTests.CheckInputs(role, Plan, now, slot);
        }
        var limited = role with { Inventory = role.Inventory with { Items =
        [
            role.Inventory.Items[0] with { Core = role.Inventory.Items[0].Core with { ExpireTime = checked((int)now + 20) } },
            new(2, new(InventoryListType.Main, 122), new(template.Id, ItemKind.Material, 2) { ExpireTime = checked((int)now + 10) }),
            new(3, new(InventoryListType.Main, 123), new(template.Id, ItemKind.Material, 2) { ExpireTime = checked((int)now + 1) })
        ] } };
        foreach (var (count, source, expected) in new (int, ushort?, long)[]
            { (1, 121, now + 20), (2, 121, now + 20), (1, null, now + 20), (3, null, now + 10) })
        {
            var plan = planner.Plan(limited, [new(limited.Id, template.Id, -count, source)], clock.GetUtcNow());
            That(plan is { CharacterPrecondition: { } guard } && guard.QualificationExpiresAt == expected &&
                guard.Matches(limited, expected - 1) && !guard.Matches(limited, expected),
                "指定源的整堆/部分消费及材料跨堆只绑定实际用到的最早期限，不绑定后续短期堆");
        }
        var reward = planner.Plan(role, [new(role.Id, 10002611, 1)], clock.GetUtcNow());
        That(reward?.CharacterPrecondition?.QualificationExpiresAt == now + 5,
            "新授予的有限实体同样不能在到期后提交");
        That(planner.Plan(limited, [], clock.GetUtcNow())?.CharacterPrecondition?.QualificationExpiresAt is null,
            "纯金币或零道具计划不绑定未参与的有限资产");

        using var f = new Fixture();
        Character Seed(Character character, DfoServer.Game.Accounts.AccountOwners.Lease owner) =>
            f.Repository.CommitInventory(owner, character, new(character.Inventory, character.Inventory with
            { Items = [new(0, slot, new(template.Id, ItemKind.Material, 2) { ExpireTime = checked((int)now + 10) })] }, "expiry_fixture"));
        var a = Seed(f.First, f.FirstOwner);
        var b = Seed(f.Second, f.SecondOwner);
        var relation = f.NewRelation(a, b);
        var created = f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, a, b, Noop(a), Noop(b), null, relation);
        a = created.First; b = created.Second!;
        var firstPlan = planner.Plan(a, [new(a.Id, template.Id, -1, 121)], clock.GetUtcNow(), 50)!;
        var secondPlan = planner.Plan(b, [], clock.GetUtcNow(), 20)!;
        clock.Advance(TimeSpan.FromSeconds(9));
        var next = relation with { Revision = relation.Revision + 1, PrepareClears = 1 };
        var committed = f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, a, b, firstPlan, secondPlan, relation, next);
        a = committed.First; b = committed.Second!;
        That(a.Inventory.Gold == 950 && b.Inventory.Gold == 980 && a.Inventory.Find(slot)!.Core.Count == 1,
            "截止前一秒双方事务可正常消费部分源并保存真实结果");
        firstPlan = planner.Plan(a, [], clock.GetUtcNow(), 50)!;
        secondPlan = planner.Plan(b, [new(b.Id, template.Id, -1)], clock.GetUtcNow(), 20)!;
        var single = planner.Plan(a, [new(a.Id, template.Id, -1, 121)], clock.GetUtcNow())!;
        long audit = f.Count("inventory_audit_log"), mail = f.Count("mailbox_messages");
        clock.Advance(TimeSpan.FromSeconds(1));
        var rejected = next with { Revision = next.Revision + 1, PrepareClears = 2 };
        Reject<InventoryQualificationExpiredException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner,
            a, b, firstPlan, secondPlan, next, rejected, [f.Delivery("expiry", b)]),
            "第二方材料恰到期时拒绝双方事务，即使第一方已开始扣款也须回滚");
        Reject<InventoryQualificationExpiredException>(() => f.Repository.CommitMarriageSingle(f.FirstOwner, a, single, next, rejected),
            "单方指定源在规划后恰到期同样拒绝");
        That(f.ReadFirst().Revision == a.Revision && f.ReadSecond().Revision == b.Revision &&
            f.ReadFirst().Inventory.Items.SequenceEqual(a.Inventory.Items) && f.ReadSecond().Inventory.Items.SequenceEqual(b.Inventory.Items) &&
            f.ReadFirst().Inventory.Gold == a.Inventory.Gold && f.ReadSecond().Inventory.Gold == b.Inventory.Gold &&
            f.Repository.FindMarriage(a.Id)?.Revision == next.Revision && f.Count("inventory_audit_log") == audit &&
            f.Count("mailbox_messages") == mail, "到期拒绝完整保留双方UID、数量、金币、revision、关系、审计与邮件");
    }
}
