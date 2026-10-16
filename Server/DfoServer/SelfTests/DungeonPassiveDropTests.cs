using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonPassiveDropTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    static DungeonRawSection Group(params int[] values) => new("special passive object item", values.Select(value => new PvfToken(0, value)).ToArray());
    static ItemTemplate Template(int id) => new(id, ItemKind.Consumable, 0, 1, 0, 1000, 1, 0, 1, "[free]", 0, 0, "合成特殊物品")
    { CreationRate = 1 };

    public static void RunCore()
    {
        int before = Count;
        var group = Group(0, -1, 2, 1000, 2500, 107000903, 7500, 1, -1, 0);
        var groups = DungeonPassiveItemGroup.Parse([group]);
        That(groups.Length == 2 && groups[0].Items[1].ItemId == 107000903 && groups[1].Items.IsEmpty,
            "指定掉落保留32位物品ID、顺序、空组和原权重");
        foreach (var values in new[] { new[] { 0, -1, 2, 1000, 1 }, new[] { 0, -1, 1, 0, 1 }, new[] { 0, -1, 1, 1000, -1 } })
            Reject<InvalidDataException>(() => DungeonPassiveItemGroup.Parse([Group(values)]), "截断、非法物品或负权重拒绝");
        var overwritten = DungeonPassiveItemGroup.Parse([group, Group(9, -1, 1, 1000, 5000)]).Single();
        That(overwritten.Index == 0 && overwritten.DeclaredIndex == 9 && overwritten.Items.Single().Weight == 5000,
            "重复块沿原生最后块替换，引用按实际序号而非已消费的首整数");
        var f = Fixture(99, 0);
        var definition = new DungeonDefinition(3, "dungeon/fixture.dgn", 1, 5, [f.Entry.Maze], [group]);
        var policy = new DungeonPassiveDropPolicy(_ => definition, Template);
        var samples = new Queue<int>([2499, 2500]);
        var prepared = policy.Prepare(f.Entry, f.Plan, bound => { That(bound == 10001, "原生指定抽取保留闭区间上界"); return samples.Dequeue(); }, Now);
        That(prepared.Count == 1 && prepared[0].Contents.Select(row => row.Item!.ItemId).SequenceEqual(new[] { 1000, 107000903 }) &&
            prepared[0].Origin.AssociationSlot == 0 && prepared[0].Origin.ParentOrdinal == 1,
            "不含item的父对象不占关联槽，指定池按一次绝对累计抽样跨条目边界");
        That(policy.Prepare(f.Entry, f.Plan, _ => 10000, Now).Single().Contents.IsEmpty,
            "权重总和10000时保留原生10000这个空区间，不自动归一到必掉");
        var two = f.Plan with { SpecialObjects = [.. f.Plan.SpecialObjects, f.Plan.SpecialObjects[1] with
        { ObjectKey = 200, Source = f.Plan.SpecialObjects[1].Source with { SourceOrdinal = 2 } }] };
        var associations = policy.Prepare(f.Entry, two, _ => 0, Now);
        That(associations.Select(row => row.Origin.AssociationSlot).SequenceEqual(new byte[] { 0, 1 }) &&
            associations.All(row => row.Origin.GroupIndex == 0), "父对象使用独立关联槽，同组重复引用不混为一个父对象");
        var multiParent = f.Plan.SpecialObjects[1];
        var multi = f.Plan with { SpecialObjects = [multiParent with { Source = multiParent.Source with
        { RawValues = new[] { 225, 400, 250, 0, 2 }, Children = [.. multiParent.Source.Children, new(1, "[item]", new[] { 1, 1, -1, -1, -1 }, null)] } }] };
        That(policy.Prepare(f.Entry, multi, _ => 0, Now).All(row => row.Origin.AssociationSlot == 0),
            "同一父对象多条item规则共用物品关联槽，空池也不改变后续关联序号");
        Reject<InvalidDataException>(() => policy.Prepare(f.Entry, f.Plan, _ => -1, Now), "抽样越界不重抽或取模");
        var tooMany = f.Plan with { SpecialObjects = Enumerable.Range(0, 3).Select(index => f.Plan.SpecialObjects[1] with
        { ObjectKey = checked((ushort)(200 + index)), Source = f.Plan.SpecialObjects[1].Source with
        { SourceOrdinal = index, Children = [new(0, "[item]", new[] { 0, 127, -1, -1, -1 }, null)] } }).ToArray() };
        Reject<InvalidDataException>(() => policy.Prepare(f.Entry, tooMany, _ => 0, Now), "完整物品上限超出255时拒绝，不截断后半批次");
        var ground = new DungeonGroundObject(Guid.NewGuid(), f.Run.Snapshot.Identity, new(f.Plan.SpecialObjects[1].ObjectKey, 400,
            f.Entry.StartMap, 400, 250, 0), new ItemCore(1000, ItemKind.Consumable, 1))
        { PassiveOrigin = prepared[0].Origin, UsesCollectionLedger = true, ResourceIdentity = "合成入房来源" };
        var state = new DungeonGroundState(f.Run, [f.Plan], [], passiveGrounds: [ground]);
        That(state.FindAvailable(f.Run.Snapshot.Identity, 400) is null, "准入未提交不可拾取");
        state.PublishPassiveEntry();
        That(state.FindAvailable(f.Run.Snapshot.Identity, 400) is null, "已提交但Loading期间仍不能拾取");
        f.Run.CompleteLoading(f.Run.Snapshot.Identity, Guid.NewGuid());
        var available = state.FindAvailable(f.Run.Snapshot.Identity, 400)!;
        var pending = state.PreparePickup(f.Run.Snapshot.Identity, available)!;
        That(state.HasPendingPickup && state.ConfirmPickup(pending) && state.FindAvailable(f.Run.Snapshot.Identity, 400) is null,
            "特殊物品使用原地面状态，只能拾取一次且未假造怪物死亡");
        var packet = DungeonPackets.StartMap(f.Entry, f.Plan, 7, passiveItems: [ground]);
        int at = 19 + (f.Plan.Monsters.Count + f.Plan.SpecialObjects.Count) * 22;
        byte[] expected = [1, 0, 0x90, 1, 0, 0, 0xE8, 3, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        That(packet.AsSpan(at, 22).SequenceEqual(expected) && packet.Length == at + 25 && packet[^1] == 255,
            "独立预期编码核对count+21B关联、u32key、模板、数量与尾部，不使用123B RAW");
        var gear = ground with { Content = new(new ItemCore(2000, ItemKind.Equipment, 123)
        { Attr = 7, Durability = 41, AmplifyType = 3, AmplifyValue = 513 }) };
        var gearPacket = DungeonPackets.StartMap(f.Entry, f.Plan, 7, passiveItems: [gear]);
        That(gearPacket.AsSpan(at + 14, 8).SequenceEqual(new byte[] { 41, 0, 3, 1, 2, 0, 0, 0 }),
            "编码器按独立预期传耐久及增幅类型/数值，不把Attr、角色ID或强化挪入；不代替客户端验收");
        var unsignedGear = gear with { Content = new(gear.Core with { AmplifyType = 128, AmplifyValue = 65535 }) };
        var unsignedPacket = DungeonPackets.StartMap(f.Entry, f.Plan, 7, passiveItems: [unsignedGear]);
        That(unsignedPacket.AsSpan(at + 16, 3).SequenceEqual(new byte[] { 128, 255, 255 }),
            "编码器保留u8/u16高位，不做有符号截断；该边界不证明增幅枚举128具有业务资格");
        That(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(at + 2)) == 400 &&
            DungeonPackets.StartMap(f.Entry, f.Plan, 7, payload: DungeonMapPayload.Cached, passiveItems: [ground]).Length == 16,
            "缓存回访仍保持16B，不重发或重抽物品列表");
        Reject<InvalidDataException>(() => DungeonPackets.StartMap(f.Entry, f.Plan, 7, passiveItems: [ground, ground]),
            "重复入房地面key拒绝");
        ObjectRules();
        Console.WriteLine($"PASS {Count - before} 特殊物品解析、抽样、父关联、21B通知和原地面生命周期断言");
    }

    static void ObjectRules()
    {
        var names = new Dictionary<int, string>();
        var source = new List<PvfToken>();
        var common = new List<PvfToken>();
        void Field(List<PvfToken> target, string tag, params int[] values)
        {
            int index = names.Count;
            names.Add(index, "[" + tag + "]");
            target.Add(new(3, index));
            target.AddRange(values.Select(value => new PvfToken(0, value)));
        }
        Field(source, "drop prob count", 1);
        Field(source, "drop prob", 1, 200, 10000, 10000, 10000, 0, 0);
        Field(source, "basis of rarity dicision", Enumerable.Repeat(1000000, 28).ToArray());
        Field(source, "dungeon difficulty drop bonusrate", Enumerable.Repeat(1, 25).ToArray());
        Field(source, "monster type drop bonusrate", Enumerable.Repeat(1, 20).ToArray());
        Field(source, "item drop ref table", 1, 0, 2);
        Field(source, "item drop rarity control", new int[5]);
        Field(common, "gold drop ref table", 1, 17, 0);
        var rules = DungeonDropRuleCatalog.Parse(source, common, index => names[index], objectRules: true);
        That(rules.RarityControl.Length == 5 && rules.PartyBonus.IsEmpty && rules.GoldReferences[0].BaseAmount == 17,
            "物件表按自己的5项控制字段解析，共享金币引用，不要求怪物表独有字段");
        var f = Fixture(99, 0);
        var parent = f.Plan.SpecialObjects[1];
        var room = f.Plan with { SpecialObjects = [parent with { Source = parent.Source with
        { Children = [new(0, "[item]", new[] { 0, 1, 1, -1, -1 }, null)] } }] };
        var definition = new DungeonDefinition(3, "dungeon/fixture.dgn", 1, 1, [f.Entry.Maze], [Group(0, -1, 1, 1000, 10001)]);
        var equipment = Template(2000) with { Kind = ItemKind.Equipment, StackLimit = 1, Durability = 41 };
        var candidates = DungeonItemDropCatalog.Create([Template(1000), equipment]);
        var policy = new DungeonPassiveDropPolicy(_ => definition, id => id == 2000 ? equipment : Template(id), rules, candidates);
        var contents = policy.Prepare(f.Entry with { BasisLevel = 1 }, room, _ => 0, Now).Single().Contents;
        That(contents.Length == 4 && contents[0].GoldAmount == 17 && contents[1].Item!.ItemId == 1000 &&
            contents[2].Item is { ItemId: 2000, Durability: 41, Value: 1 } && contents[3].Item!.ItemId == 1000,
            "通用金币、堆叠品、独立装备和指定池依序完整生成，两个次数不互相替代");
    }

    static (DungeonEntryPlan Entry, DungeonMonsterPlan Plan, SinglePlayerDungeonRun Run) Fixture(ushort id, long revision,
        ushort firstKey = 100)
    {
        var map = new DungeonMap("boss", new(0, 0), 1, "map/passive-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "AA", [map.Position], [map.Position], [map], []);
        var source = new DungeonRoomMonster(1, new[] { 1, 0, 5, 500, 250, 0, 1, 1 }, "[fixed]", "[boss]", "monster/fixture.mob");
        var room = new DungeonRoomDefinition(map, [source], [], [])
        {
            SpecialObjects = [new(0, 221, new[] { 221, 300, 250, 0, 0 }, "passiveobject/fixture.obj", []),
                new(1, 225, new[] { 225, 400, 250, 0, 1 }, "passiveobject/tree.obj",
                    [new(0, "[item]", new[] { 0, 2, -1, -1, -1 }, null)])]
        };
        var plan = DungeonMonsterPlan.FromRoom(room, id, firstKey, 5);
        var entry = new DungeonEntryPlan(id, revision, 3, 0, 255, 0, maze, map, map) { BasisLevel = 5 };
        var authority = new NativeDungeonCombatAuthority();
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out string reason) || !authority.BindTo(run!))
            throw new InvalidOperationException("特殊物品合成Run失败: " + reason);
        return (entry, plan, run!);
    }
}
