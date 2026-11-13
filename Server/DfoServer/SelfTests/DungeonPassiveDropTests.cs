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
        var unsignedGear = gear with { Content = new(gear.Core with { AmplifyType = 4, AmplifyValue = 65535 }) };
        var unsignedPacket = DungeonPackets.StartMap(f.Entry, f.Plan, 7, passiveItems: [unsignedGear]);
        That(unsignedPacket.AsSpan(at + 16, 3).SequenceEqual(new byte[] { 4, 255, 255 }),
            "已净化属性的u16基础值保留高位，不做有符号截断");
        var hiddenGear = gear with { Content = new(gear.Core with { AmplifyType = 0x83, AmplifyValue = 65535 }) };
        var hiddenPacket = DungeonPackets.StartMap(f.Entry, f.Plan, 7, passiveItems: [hiddenGear]);
        That(hiddenPacket.AsSpan(at + 16, 3).SequenceEqual(new byte[] { 128, 0, 0 }) &&
            hiddenGear.Core.AmplifyType == 0x83 && hiddenGear.Core.AmplifyValue == 65535,
            "入房短行隐藏未净化类型和数值，不改写内部隐藏核心");
        That(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(at + 2)) == 400 &&
            DungeonPackets.StartMap(f.Entry, f.Plan, 7, payload: DungeonMapPayload.Cached, passiveItems: [ground]).Length == 16,
            "缓存回访仍保持16B，不重发或重抽物品列表");
        Reject<InvalidDataException>(() => DungeonPackets.StartMap(f.Entry, f.Plan, 7, passiveItems: [ground, ground]),
            "重复入房地面key拒绝");
        ObjectRules();
        EquipmentControl();
        Console.WriteLine($"PASS {Count - before} 特殊物品解析、抽样、父关联、21B通知和原地面生命周期断言");
    }

    static void EquipmentControl()
    {
        var f = Fixture(99, 0);
        var parent = f.Plan.SpecialObjects[1];
        var room = f.Plan with { SpecialObjects = [parent with { Source = parent.Source with
        { Children = [new(0, "[item]", new[] { 0, 1, 0, -1, -1 }, null)] } }] };
        var epic = Template(2001) with { Kind = ItemKind.Equipment, Rarity = 4, StackLimit = 1, Durability = 41 };
        var material = Template(3001) with { Kind = ItemKind.Material };
        var definition = new DungeonDefinition(3, "dungeon/passive-control.dgn", 1, 1, [f.Entry.Maze],
            [Group(0, -1, 2, epic.Id, 1000, material.Id, 2000)]);
        var settings = new DungeonDropControlSettings();
        var policy = new DungeonPassiveDropPolicy(_ => definition, id => id == epic.Id ? epic : material, dropControl: settings);
        var baseline = policy.Prepare(f.Entry, room, _ => 0, Now).Single().Contents;
        settings.Replace([new(DungeonDropScope.All, [], new(Equipment: 2, Special: 100))], true);
        var frozen = policy.PrepareEntry(f.Entry);
        That(frozen(room, bound => bound == 10001 ? 1999 : bound == 3000 ? 2999 : 0, Now).Single().Contents
            is [{ Item.ItemId: 2001 }], "指定物件史诗新增概率边界可命中，只用Equipment而不叠Special");
        That(frozen(room, bound => bound == 10001 ? 2000 : 0, Now).Single().Contents is [{ Item.ItemId: 3001 }] &&
            frozen(room, bound => bound == 10001 ? 4000 : 0, Now).Single().Contents.IsEmpty,
            "指定池史诗翻倍，原材料绝对概率及互斥空结果保持");
        settings.Replace([new(DungeonDropScope.All, [], new(Equipment: 0))], true);
        That(frozen(room, _ => 0, Now).Single().Contents.SequenceEqual(baseline) &&
            policy.PrepareEntry(f.Entry)(room, _ => 0, Now).Single().Contents is [{ Item.ItemId: 3001 }],
            "配置热改不追改已准备房间，下一次入场关闭物件史诗而保留材料");
        var inherited = DungeonDropControlSnapshot.Apply(new("inherited", []), new(Equipment: 2));
        That(policy.PrepareEntry(f.Entry, inherited)(room, _ => 0, Now).Single().Contents is [{ Item.ItemId: 2001 }],
            "正式入场物件沿用怪物同次冻结规则，不二次读取已热改配置");
        settings.Replace([new(DungeonDropScope.All, [4], new(Equipment: 0))], true);
        That(policy.Prepare(f.Entry, room, _ => 0, Now).Single().Contents.SequenceEqual(baseline),
            "物件遵循指定副本集合，范围外不关闭史诗");
        var oversized = definition with { RawSections = [Group(0, -1, 3, material.Id, 10001, epic.Id, 1000, 4001, 1000)] };
        settings.Replace([new(DungeonDropScope.All, [], new(Equipment: 100))], true);
        var clipped = new DungeonPassiveDropPolicy(_ => oversized, id => id == epic.Id ? epic : material with { Id = id }, dropControl: settings);
        That(clipped.Prepare(f.Entry, room, _ => 0, Now).Single().Contents is [{ Item.ItemId: 3001 }],
            "物件只控制原10001可达区间，尾部原不可达史诗不被倍率新增");
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
        int rarityStart = source.FindIndex(token => token.Type == 3 && names[token.Value] == "[basis of rarity dicision]") + 1;
        var restrictionSource = source.ToArray();
        for (int index = 0; index < 28; index++)
            restrictionSource[rarityStart + index] = new(0, index % 7 < 3 ? 0 : index % 7 == 3 ? 500000 : 1000000);
        DisjointRestrictions(DungeonDropRuleCatalog.Parse(restrictionSource, common, index => names[index], objectRules: true));
    }

    static void DisjointRestrictions(DungeonDropRuleCatalog rules)
    {
        var f = Fixture(99, 0);
        var epic = Template(2001) with { Kind = ItemKind.Equipment, Rarity = 4, StackLimit = 1, Durability = 41 };
        var unique = epic with { Id = 2002, Rarity = 3, Durability = 37 };
        var material = Template(3001) with { Kind = ItemKind.Material, Rarity = 4 };
        var otherMaterial = material with { Id = 3002, Rarity = 3 };
        var templates = new[] { epic, unique, material, otherMaterial };
        var originals = templates.ToArray();
        var definition = new DungeonDefinition(3, "dungeon/restricted-passive-fixture.dgn", 1, 1, [f.Entry.Maze],
            [Group(0, -1, 3, epic.Id, 3000, unique.Id, 3000, material.Id, 4001)]);
        var policy = new DungeonPassiveDropPolicy(_ => definition, id => templates.Single(template => template.Id == id),
            rules, DungeonItemDropCatalog.Create(templates));
        var entry = f.Entry with { BasisLevel = 1 };
        var restricted = entry with { DisjointForbiddenDropRarities = ImmutableHashSet.Create(4) };
        CheckSource(random: true);
        CheckSource(random: false);
        That(templates.SequenceEqual(originals) && templates.All(template => !template.DisjointForbidden),
            "物件来源限制只写本次装备实例，共享模板及后续普通掉落不被污染");

        void CheckSource(bool random)
        {
            string label = random ? "通用物件" : "指定物件";
            var parent = f.Plan.SpecialObjects[1];
            var room = f.Plan with { SpecialObjects = [parent with { Source = parent.Source with
            { Children = [new(0, "[item]", new[] { 0, random ? 0 : 4, random ? 2 : 0, -1, -1 }, null)] } }] };
            var normal = Prepare(entry);
            var limited = Prepare(restricted);
            var after = Prepare(entry);
            int expected = random ? 6 : 4;
            That(normal.Contents.Length == expected && limited.Contents.Length == expected &&
                normal.Draws.SequenceEqual(limited.Draws) && normal.Draws.SequenceEqual(after.Draws),
                label + "来源限制不改变完整数量、抽样调用顺序、区间或结果");
            That(normal.Contents.SequenceEqual(after.Contents) && normal.Contents.All(content => content.Item?.DisjointForbidden != true) &&
                normal.Contents.Count(content => content.Item?.ItemId == epic.Id) == (random ? 1 : 2) &&
                normal.Contents.Count(content => content.Item?.ItemId == unique.Id) == 1 &&
                normal.Contents.Count(content => content.Item?.ItemId == material.Id) == 1,
                label + "保留史诗、其它稀有度装备及同稀有度材料，受限后普通来源仍可分解");
            That(normal.Contents.Zip(limited.Contents).All(pair => pair.First.Item?.ItemId == epic.Id
                    ? pair.Second.Item is { DisjointForbidden: true } &&
                        pair.First == pair.Second with { Item = pair.Second.Item with { DisjointForbidden = false } }
                    : pair.First == pair.Second),
                label + "仅命中史诗装备实例禁止分解，完整Core、品质、材料和金币保持原值");

            (ImmutableArray<DungeonGroundContent> Contents, List<(int Bound, int Value)> Draws) Prepare(DungeonEntryPlan sourceEntry)
            {
                var draws = new List<(int Bound, int Value)>();
                int rarity = 0, specified = 0;
                int Draw(int bound)
                {
                    int value = bound == 1000000 ? (rarity++ < 2 ? 0 : 500000) :
                        bound == 10001 ? new[] { 0, 3000, 6000, 0 }[specified++] :
                        bound == 999999997 ? 24680 : 0;
                    draws.Add((bound, value));
                    return value;
                }
                return (policy.Prepare(sourceEntry, room, Draw, Now).Single().Contents, draws);
            }
        }
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
