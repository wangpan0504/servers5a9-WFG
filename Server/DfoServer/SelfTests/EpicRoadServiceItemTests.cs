using System.Collections.Immutable;
using System.Net;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoadServiceItemTests
{
    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources) =>
        ConsumableUseSessionTests.RunEpicRoadServiceItems(towns, characters, resources);
}

internal static partial class ConsumableUseSessionTests
{
    // 与历史etc精确映射对应；不向生产目录注入任何材料定义或跨界白名单。
    const int EpicPurifyBook = 490013751, EpicInvestBook = 490013810, EpicTransportStone = 490013724;
    const int AmplificationTarget = 31440, TransportTarget = 100170570;

    internal static void RunEpicRoadServiceItems(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        int checks = Count;
        That(resources.Amplifications.Get(AmplificationOperation.Purify, EpicPurifyBook) is
            { Operation: AmplificationOperation.Purify, Count: 1, Option: 0, Village: true, SeriaRoom: true } &&
            resources.Amplifications.Get(AmplificationOperation.Invest, EpicInvestBook) is
            { Operation: AmplificationOperation.Invest, Count: 1, Option: 5, Village: true, SeriaRoom: true } &&
            resources.Amplifications.Get(AmplificationOperation.Twist, EpicInvestBook) is null &&
            resources.Amplifications.Get(AmplificationOperation.GoldInvest, EpicInvestBook) is null &&
            resources.Amplifications.Rules.MinimumLevel == 55,
            "真实751/810按原etc进入Purify1及Invest1/all5，不从说明伪造Twist或随机增幅");
        That(resources.Items.ActionConsumable(EpicTransportStone) is
            { Label: "[cargo transport stone]", Parameters: [4040] }, "真实724进入原跨界动作目录并指向4040");
        var rule = resources.EquipmentTransports.Rules[4040];
        That(rule.Grades.SequenceEqual(new[] { 95, 95, 1 }) && rule.RarityWeights.Count == 1 &&
            rule.RarityWeights.GetValueOrDefault(4) == 1 && rule.Included.Count == 336 &&
            rule.EquipmentSlots.SetEquals(new byte[] { 12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 25 }) &&
            rule.Included.Contains(TransportTarget) && !rule.Included.Contains(AmplificationTarget) &&
            rule.Excluded.IsEmpty && rule.ExcludedProperties.SetEquals(new[] { "growthType", "periodEquipment" }) &&
            rule.StoneCost(resources.Items.Get(TransportTarget)) == 1 && rule.StoneCost(resources.Items.Get(AmplificationTarget)) == 0,
            "原4040的95级史诗、十二部位、336白名单和排除属性全部保留，不泛放全史诗");

        foreach (bool purify in new[] { true, false }) Amplification(purify);
        Transport();
        Console.WriteLine($"PASS {Count - checks} 星河751/810/724真实目录、Session与Handler/Sender使用及SQLite重载/原子拒绝断言（非socket TCP或实机）");

        void Amplification(bool purify)
        {
            int book = purify ? EpicPurifyBook : EpicInvestBook;
            var operation = purify ? AmplificationOperation.Purify : AmplificationOperation.Invest;
            using var f = new Fixture(towns, characters, level: 95);
            f.Select();
            SeedServiceItems(f, resources, [new(book, 3), new(AmplificationTarget, 2)]);
            var selected = f.Session.Selected!;
            var targets = selected.Inventory.Items.Where(item => item.Core.ItemId == AmplificationTarget)
                .OrderBy(item => item.Position.Index).ToArray();
            That(targets.Length == 2, "活动书用例持有两件真实31440目标");
            ChangeServiceFixture(f, inventory => inventory with
            {
                Items = inventory.Items.Select(item => item.Core.ItemId != AmplificationTarget ? item : item with
                {
                    Core = item.Core with { Attr = 0xE0, EnchantCardId = 3175, EnchantUpgrade = 2, Rune = 8,
                        GenuineUpgrade = 4, Extension114 = new(1, 333),
                        AmplifyType = purify ? (byte)0x83 : (byte)0, AmplifyValue = purify ? (ushort)51 : (ushort)0 }
                }).ToImmutableArray()
            });
            var before = f.Session.Selected!;
            var source = before.Inventory.Items.Single(item => item.Core.ItemId == book);
            var target = before.Inventory.Find(targets[0].Position)!;
            var request = new AmplificationRequest(operation, target.Position.Index, target.Core.ItemId,
                source.Position.Index, book, purify ? (byte)0 : (byte)4);
            var service = new InventoryAmplificationService(resources.Items, resources.Amplifications,
                resources.Enhancements, f.Clock);
            InventoryResult Use(AmplificationRequest input) => f.Session.PlanAndCommitInventory(
                character => service.Plan(character, input, out _), InventoryAmplificationService.Rejection);

            var result = Use(request);
            ushort expectedValue = purify ? (ushort)51 : resources.Amplifications.Rules.BaseValue(4, resources.Items.Get(AmplificationTarget).Rarity);
            var expectedTarget = target with { Core = target.Core with { AmplifyType = purify ? (byte)3 : (byte)4, AmplifyValue = expectedValue } };
            var expectedSource = source with { Core = source.Core with { Value = source.Core.Count - 1 } };
            That(expectedValue > 0 && result.Accepted && result.Receipt is { } receipt && f.Session.CanPublishInventory(receipt) &&
                f.Session.Selected!.Inventory.Find(target.Position) == expectedTarget &&
                f.Session.Selected.Inventory.Find(source.Position) == expectedSource &&
                f.Session.Selected.Inventory.Gold == before.Inventory.Gold &&
                f.Session.Selected.Inventory.AccountWallet.Same(before.Inventory.AccountWallet),
                "真实活动书精确扣原UID一张，目标仅改已证异次元字段且保完整Core/金币/钱包：" + book);
            That(Same(f.Session.Selected!, ReopenServiceCharacter(f)), "活动书效果及扣源经新Repository完整恢复：" + book);
            string unchanged = ServiceItemAssets(f);
            var repeated = Use(request);
            That(!repeated.Accepted && repeated.Error == (purify ? 23 : 20) && ServiceItemAssets(f) == unchanged && Same(f.Read(), f.Session.Selected!),
                "净化后的装备不能再净化、已赋予装备不能再次Invest，非法重复不扣第二张：" + book);

            // 第二件保留原合法状态；810额外证明污染目标不因书的说明文本被误当净化或扭转。
            var second = f.Session.Selected!.Inventory.Find(targets[1].Position)!;
            var secondRequest = request with { TargetSlot = second.Position.Index };
            if (!purify)
            {
                ChangeServiceFixture(f, inventory => inventory with { Items = inventory.Items.Replace(second,
                    second with { Core = second.Core with { AmplifyType = 0x83, AmplifyValue = 51 } }) });
                unchanged = ServiceItemAssets(f);
                var deniedInfected = Use(secondRequest);
                That(!deniedInfected.Accepted && deniedInfected.Error == 21 && ServiceItemAssets(f) == unchanged,
                    "真实810拒绝被污染装备，全部资产与revision不变");
                var infected = f.Session.Selected!.Inventory.Find(second.Position)!;
                ChangeServiceFixture(f, inventory => inventory with { Items = inventory.Items.Replace(infected, second) });
            }
            var stale = f.Session.Selected!;
            var stalePlan = service.Plan(stale, secondRequest, out _);
            That(stalePlan.Accepted, "旧revision验证先形成真实活动书的完整合法计划：" + book);
            ChangeServiceFixture(f, inventory => inventory with { Gold = checked(inventory.Gold + 1) });
            var current = f.Session.Selected!;
            unchanged = ServiceItemAssets(f);
            bool rejected = false;
            try { _ = InventoryService.Commit(f.Repository, f.Owner, stale, stalePlan); }
            catch (InvalidOperationException) { rejected = true; }
            That(rejected && ServiceItemAssets(f) == unchanged && ReferenceEquals(current, f.Session.Selected) &&
                Same(current, ReopenServiceCharacter(f)), "真实活动书旧revision计划整笔拒绝，不改源/目标/审计/当前Session：" + book);

            // 复用仍合法的第二目标，补实际活动源经Handler的应答/消费组合；不复制通用TCP矩阵。
            var wireBefore = f.Session.Selected!;
            var wireSource = wireBefore.Inventory.Find(source.Position)!;
            var wireTarget = wireBefore.Inventory.Find(second.Position)!;
            ushort opcode = purify ? (ushort)204 : (ushort)205;
            byte[] body = DfoServer.Testing.TcpPeer.Build(w =>
            {
                if (!purify) w.Write((byte)0);
                w.Write(second.Position.Index); w.Write(AmplificationTarget);
                w.Write(source.Position.Index); w.Write(book);
                if (!purify) w.Write((byte)4);
            });
            var wire = EpicItemWire(f, towns, characters, resources, opcode, body);
            byte[] ack = DfoServer.Testing.TcpPeer.Build(w =>
            {
                w.Write((byte)1); if (!purify) w.Write((byte)0);
                w.Write(source.Position.Index); w.Write(wireSource.Core.Count - 1);
                w.Write(second.Position.Index); w.Write(purify ? (byte)3 : (byte)4); w.Write(expectedValue);
            });
            That(wire.Result == CommandResult.Handled && wire.Frames.Count == 1 &&
                wire.Frames[0] is { Kind: 1 } frame && frame.Opcode == opcode && frame.Body.AsSpan().SequenceEqual(ack) &&
                f.Session.Selected!.Revision == wireBefore.Revision + 1 &&
                f.Session.Selected.Inventory.Find(source.Position) == wireSource with { Core = wireSource.Core with { Value = wireSource.Core.Count - 1 } } &&
                f.Session.Selected.Inventory.Find(second.Position) == wireTarget with { Core = wireTarget.Core with
                    { AmplifyType = purify ? (byte)3 : (byte)4, AmplifyValue = expectedValue } } &&
                SameServiceCharacter(f.Session.Selected, ReopenServiceCharacter(f)),
                "真实751/810原body经Registry/Handler精确扣一并回原位ACK，完整实例恢复且无重复源/目标NOTI14：" + book);
            unchanged = ServiceItemAssets(f);
            wire = EpicItemWire(f, towns, characters, resources, opcode, body);
            That(wire.Result == CommandResult.Handled && wire.Frames.Count == 1 &&
                wire.Frames[0].Kind == 1 && wire.Frames[0].Opcode == opcode &&
                wire.Frames[0].Body.AsSpan().SequenceEqual(new byte[] { 0, purify ? (byte)23 : (byte)20, 0 }) &&
                ServiceItemAssets(f) == unchanged,
                "实际活动书成功后的同body重放只发原拒绝ACK，完整资产/审计/序列不变：" + book);

        }

        void Transport()
        {
            using var f = new Fixture(towns, characters, level: 95);
            f.Select();
            SeedServiceItems(f, resources, [new(EpicTransportStone, 3), new(TransportTarget, 2), new(AmplificationTarget, 1)]);
            var first = f.Session.Selected!.Inventory.Items.Where(item => item.Core.ItemId == TransportTarget)
                .OrderBy(item => item.Position.Index).ToArray();
            var original = first[0].Core with { Attr = 0xE0, EnchantCardId = 3175, Rune = 9,
                Extension97 = 0x12345678, GenuineUpgrade = 2, DisjointForbidden = true };
            ChangeServiceFixture(f, inventory => inventory with
            {
                AccountCargo = inventory.AccountCargo with { Capacity = 8 },
                Items = inventory.Items.Replace(first[0], first[0] with { Core = original }).AddRange(
                    Enumerable.Range(1, 7).Select(index => new InventoryItem(0,
                        new(InventoryListType.AccountCargo, (short)index), original)))
            });
            var source = f.Session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == EpicTransportStone);
            var target = f.Session.Selected.Inventory.Find(first[0].Position)!;
            var bad = f.Session.Selected.Inventory.Items.Single(item => item.Core.ItemId == AmplificationTarget);
            var service = new EquipmentTransportService(resources.Items.Get, resources.Items.ActionConsumable,
                resources.EquipmentTransports, resources.EpicPieces, f.Repository, f.Clock);
            CreatureTransportRequest Request(short targetSlot) => new((ushort)source.Position.Index, (ushort)targetSlot, 0, uint.MaxValue);
            string unchanged = ServiceItemAssets(f);
            var deniedBad = f.Session.TransportEquipment(service, Request(bad.Position.Index));
            That(!deniedBad.Inventory.Accepted && deniedBad.Inventory.Error == 7 && ServiceItemAssets(f) == unchanged,
                "真实724拒绝4040白名单外31440，源石/装备/金库/审计/revision不变");
            var before = f.Session.Selected!;
            var used = f.Session.TransportEquipment(service, Request(target.Position.Index));
            var destination = new InventorySlot(InventoryListType.AccountCargo, 0);
            That(used.Inventory.Accepted && f.Session.Selected!.Inventory.Find(source.Position) == source with
                { Core = source.Core with { Value = source.Core.Count - 1 } } &&
                f.Session.Selected.Inventory.Find(target.Position) is null &&
                f.Session.Selected.Inventory.Find(destination) == target with { Position = destination } &&
                f.Session.Selected.Inventory.Gold == before.Inventory.Gold &&
                f.Session.Selected.Inventory.AccountWallet.Same(before.Inventory.AccountWallet),
                "真实724精确扣一，原UID及含禁分解位的完整Core进入唯一空账号金库格，不扣金币或灵魂");
            That(SameServiceCharacter(f.Session.Selected!, ReopenServiceCharacter(f)), "真实724移动、扣石及完整Core经新Repository恢复");
            // 独立越过真实石头冷却后才验容量，防止其它使用门禁伪装成满仓拒绝。
            var stoneDefinition = resources.Items.ActionConsumable(EpicTransportStone)!;
            f.Clock.Advance(TimeSpan.FromMilliseconds((long)stoneDefinition.CoolTime + 1));
            unchanged = ServiceItemAssets(f);
            var deniedFull = f.Session.TransportEquipment(service, Request(first[1].Position.Index));
            That(!deniedFull.Inventory.Accepted && deniedFull.Inventory.Error == 19 &&
                ServiceItemAssets(f) == unchanged && SameServiceCharacter(f.Read(), f.Session.Selected!),
                "真实724在8格已满金库拒绝第二件，不能先扣石或丢目标");

            // 只回置本隔离夹具先前搬入的同一实例，以建立第二个合法计划；不伪造规则或目标模板。
            var moved = f.Session.Selected!.Inventory.Find(destination)!;
            ChangeServiceFixture(f, inventory => inventory with { Items = inventory.Items.Replace(moved, moved with { Position = target.Position }) });
            var stale = f.Session.Selected!;
            That(service.Plan(stale, null, Request(first[1].Position.Index), "[seria room]", out var stalePlan) == 0,
                "724旧revision回归先通过真实目录、地点与空容量的完整规划");
            ChangeServiceFixture(f, inventory => inventory with { Gold = checked(inventory.Gold + 1) });
            var current = f.Session.Selected!;
            unchanged = ServiceItemAssets(f);
            bool rejected = false;
            try { _ = service.Commit(f.Owner, stalePlan!); }
            catch (InvalidOperationException) { rejected = true; }
            That(rejected && ServiceItemAssets(f) == unchanged && ReferenceEquals(current, f.Session.Selected) &&
                SameServiceCharacter(current, ReopenServiceCharacter(f)), "真实724旧revision整笔拒绝，源石/原UID/金库/审计与当前Session保持");

            var wireBefore = f.Session.Selected!;
            var wireSource = wireBefore.Inventory.Find(source.Position)!;
            var wireTarget = wireBefore.Inventory.Find(first[1].Position)!;
            byte[] body = DfoServer.Testing.TcpPeer.Build(w =>
            { w.Write((ushort)source.Position.Index); w.Write((ushort)first[1].Position.Index); w.Write((byte)0); w.Write(uint.MaxValue); });
            var wire = EpicItemWire(f, towns, characters, resources, 557, body);
            byte[] ack = DfoServer.Testing.TcpPeer.Build(w =>
            { w.Write((byte)1); w.Write((ushort)source.Position.Index); w.Write((ushort)first[1].Position.Index);
                w.Write((ushort)(wireSource.Core.Count - 1)); w.Write((ushort)0); w.Write((byte)0); });
            That(wire.Result == CommandResult.Handled && wire.Frames.Count == 3 &&
                wire.Frames[0].Kind == 1 && wire.Frames[0].Opcode == 557 && wire.Frames[0].Body.AsSpan().SequenceEqual(ack) &&
                wire.Frames[1].Kind == 0 && wire.Frames[1].Opcode == 1681 &&
                wire.Frames[2].Kind == 0 && wire.Frames[2].Opcode == 14 &&
                f.Session.Selected!.Revision == wireBefore.Revision + 1 &&
                f.Session.Selected.Inventory.Find(source.Position) == wireSource with { Core = wireSource.Core with { Value = wireSource.Core.Count - 1 } } &&
                f.Session.Selected.Inventory.Find(first[1].Position) is null &&
                f.Session.Selected.Inventory.Find(destination) == wireTarget with { Position = destination } &&
                SameServiceCharacter(f.Session.Selected, ReopenServiceCharacter(f)),
                "实际724原9B请求经Handler入账号库；ACK先于Main清槽/金库完整差量，UID及完整Core持久保留");
            That(wire.Frames[1].Body.AsSpan().SequenceEqual(DfoServer.Testing.TcpPeer.Build(w =>
                { w.Write((byte)0); w.Write((ushort)1); w.Write(first[1].Position.Index); w.Write(-1); w.Write(new byte[117]); })) &&
                wire.Frames[2].Body.Length == 126 && wire.Frames[2].Body[0] == 12 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(wire.Frames[2].Body.AsSpan(1)) == 1 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(wire.Frames[2].Body.AsSpan(3)) == 0 &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(wire.Frames[2].Body.AsSpan(5)) == TransportTarget,
                "724源空槽为完整123B清除记录，账号库差量只含实际目的格和真实目标，不二次扣石");
            unchanged = ServiceItemAssets(f);
            wire = EpicItemWire(f, towns, characters, resources, 557, body);
            That(wire.Result == CommandResult.Handled && wire.Frames.Count == 1 && wire.Frames[0].Kind == 1 &&
                wire.Frames[0].Opcode == 557 && wire.Frames[0].Body.AsSpan().SequenceEqual(new byte[] { 0, 7, 0 }) &&
                ServiceItemAssets(f) == unchanged, "724同body重放因原Main目标已空只拒绝，不重复扣石或复制装备");

        }
    }


    // 沿既有商店Handler自测的MemoryStream/Sender方式；没有socket或新增测试组。
    static (CommandResult Result, List<(byte Kind, ushort Opcode, byte[] Body)> Frames) EpicItemWire(
        Fixture f, TownCatalog towns, CharacterCatalog characters, ItemResources resources, ushort opcode, byte[] body)
    {
        return Dispatch().GetAwaiter().GetResult();
        async Task<(CommandResult Result, List<(byte Kind, ushort Opcode, byte[] Body)> Frames)> Dispatch()
        {
            var stats = new CharacterStatComputer(characters);
            var town = new TownProjection(towns, stats, null, resources.EpicPieces, clock: f.Clock);
            var projection = new InventoryProjection(stats, town, resources.EpicPieces);
            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(f.Owner, output, stop, 0,
                (kind, op, _, bytes, _) => sent.Add((kind, op, bytes.ToArray())));
            var context = new GameConnectionContext(new(923, IPAddress.Loopback, output, stop), f.Owner, f.Session, sender);
            using var log = new ServerLog(Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "item-wire-log"));
            var commands = new GameCommandRegistry();
            if (opcode == 44) new ConsumableUseHandler(f.Service, log).Register(commands);
            else if (opcode is 204 or 205)
                new AmplificationHandler(new(resources.Items, resources.Amplifications, resources.Enhancements, f.Clock),
                    projection, log).Register(commands);
            else if (opcode == 557)
                new CreatureTransportHandler(new(resources.Items.Get, resources.Items.ActionConsumable, resources.Creatures,
                    f.Repository, () => new(1), f.Clock), projection, log,
                    new(resources.Items.Get, resources.Items.ActionConsumable, resources.EquipmentTransports,
                        resources.EpicPieces, f.Repository, f.Clock)).Register(commands);
            else throw new ArgumentOutOfRangeException(nameof(opcode));
            commands.Seal();
            var result = await commands.DispatchAsync(context, new Packet(1, opcode, body));
            await sender.DrainAsync();
            That(output.Length == sent.Sum(frame => 16L + frame.Body.Length),
                "Handler应答已由真实Sender写出，关闭分支不把排队或未发送算成功");
            return (result, sent);
        }
    }

    static void SeedServiceItems(Fixture f, ItemResources resources, InventoryReward[] rewards) =>
        That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            rewards, resources.Items, resources.GoldLimits, resources.EpicPieces, f.Clock.GetUtcNow())).Accepted,
            "隔离夹具用真实模板及公共发奖器准备活动源和目标");

    static void ChangeServiceFixture(Fixture f, Func<InventorySnapshot, InventorySnapshot> change) =>
        That(f.Session.MutateInventory(character => new(true, 0, new(character.Inventory,
            change(character.Inventory), "epic_service_fixture"))).Accepted, "仅准备隔离夹具状态");

    // 提交快照保留原数组位置，读库按容器/格子排序；仍比较每件UID/完整Core及全部角色字段。
    static bool SameServiceCharacter(Character first, Character second)
    {
        Character Ordered(Character character) => character with { Inventory = character.Inventory with
        {
            Items = character.Inventory.Items.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray()
        } };
        return Same(Ordered(first), Ordered(second));
    }

    static Character ReopenServiceCharacter(Fixture f) => new CharacterRepository(f.DatabasePath).LoadCharacter(f.Owner, 0);

    static string ServiceItemAssets(Fixture f)
    {
        // 角色快照包含共享金库、钱包及源/目标；再覆盖流水与UID序列，拒绝不能留下隐藏写入。
        var result = new List<object?> { JsonSerializer.Serialize(ReopenServiceCharacter(f)) };
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.DatabasePath,
            Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        foreach (string table in new[] { "character_inventory_items", "character_inventory_slots", "inventory_audit_log", "currency_audit_log", "sqlite_sequence" })
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
            result.Add(rows);
        }
        return JsonSerializer.Serialize(result);
    }
}
