using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoadRecoveryTests
{
    internal const int Potion = 490013727;
    // 独立测试oracle；生产名单必须由原资源传入，不能使用本数组作玩法配置。
    internal static readonly uint[] OriginalExclusions = [210, 211, 212, 213, 214, 215, 216, 218, 219, 220, 221, 222, 223, 224,
        5054, 5055, 5056, 5057, 5058, 5059, 5060, 5061, 5062, 5063, 5064, 5065, 5066, 5310, 5311, 5312, 5313];

    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources, PvfArchive archive) =>
        ConsumableUseSessionTests.RunEpicRoadRecovery(towns, characters, resources, archive);

    public static void RunRaid(GameSession session, CharacterRepository repository, AccountOwners.Lease owner, ItemResources resources) =>
        ConsumableUseSessionTests.RunEpicRoadRecoveryRaid(session, repository, owner, resources);
}

internal static partial class ConsumableUseSessionTests
{
    public static void RunEpicRoadRecovery(TownCatalog towns, CharacterCatalog characters, ItemResources resources, PvfArchive archive)
    {
        int checks = Count;
        var failures = new List<string>();
        That(resources.RecoveryConsumables.TryGet(EpicRoadRecoveryTests.Potion, out var potion), "星河727真实资源进入普通消耗品目录");
        That(resources.Items.Get(potion.ItemId).Kind == ItemKind.Consumable && potion is
            { RevivalBlessing: false, CoolTime: 30000, CoolGroup: 865, EffectMilliseconds: 30000, EffectMaintenance: 0, CooltimeMaintenance: 0 } &&
            potion.StatChanges.SequenceEqual(new[] { new ConsumableStatChange("+", 100, "move speed"),
                new ConsumableStatChange("+", 100, "attack speed"), new ConsumableStatChange("+", 100, "cast speed") }),
            "星河727真实普通三速药保持原30000ms、865组、三项原值及零维护位");
        Check(potion.ExcludedDungeons.SetEquals(EpicRoadRecoveryTests.OriginalExclusions), "原31条显式禁图完整进入普通药定义");
        using (var f = new Fixture(towns, characters, hp: potion))
        {
            Enter(f);
            var usedAt = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            var used = f.Use();
            Check(used.Outcome == ConsumableUseOutcome.Committed && f.Count == 2 && used.Guard is { } guard &&
                f.Session.CanPublishConsumable(guard) && f.Read().Inventory.ConsumableStates.Any(row =>
                    row.Kind == ConsumableStateKind.Effect && row.ItemId == potion.ItemId && row.UntilMilliseconds == usedAt + 30000 && row.Maintenance == 0),
                "原727在不属31禁图的真实普通DGN3成功扣一件并保存30秒药效，raid限制不误拒普通实例");
            var cooled = f.Read();
            Check(f.Use(1).Outcome == ConsumableUseOutcome.Rejected && Same(cooled, f.Read()) && Same(cooled, f.Session.Selected!) && f.Count == 2,
                "原727同组新token在30秒冷却内拒绝，完整源/效果/revision不变");
            That(f.Session.ReturnFromFormalDungeon() is not null, "原727用例经正式回城边界退出实例");
            Check(f.Count == 2 && f.Read().Inventory.ConsumableStates.All(row => row.ItemId != potion.ItemId),
                "原0维护位使727药效在实际回城时清除，不续期或补药");
        }
        // 仅私有测试定义额外加入当前真实DGN3，独立定位Session普通药漏判；不声称3属于原31表。
        using (var f = new Fixture(towns, characters, hp: potion with { ExcludedDungeons = potion.ExcludedDungeons.Add(3) }))
        {
            Enter(f);
            var before = f.Read();
            var denied = f.Use();
            Check(denied.Outcome == ConsumableUseOutcome.Rejected && denied.Inventory is null && f.Count == 3 &&
                Same(before, f.Read()) && Same(before, f.Session.Selected!),
                "普通727私有定义显式排除当前真实DGN3时完整拒绝，源UID/Core/数量、效果、冷却、revision不变");
        }
        That(failures.Count == 0, "星河727资源传输与真实普通副本用药边界：" + string.Join(" | ", failures));
        // 使用本方法既有Enter和真实727定义，仅补原body→Handler→落库，不新增TCP入口。
        using (var f = new Fixture(towns, characters, hp: potion))
        {
            Enter(f);
            var before = f.Read();
            long usedAt = f.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            byte[] body = Convert.FromHexString("410000010000001F04351D00000000");
            var wire = EpicItemWire(f, towns, characters, resources, 44, body);
            var after = f.Read();
            That(wire.Result == DfoServer.Network.Protocol.CommandResult.Handled && wire.Frames.Count == 1 &&
                wire.Frames[0].Kind == 1 && wire.Frames[0].Opcode == 44 &&
                wire.Frames[0].Body.AsSpan().SequenceEqual(Convert.FromHexString("01410000010000001F04351D")) &&
                after.Revision == before.Revision + 1 && f.Count == 2 &&
                after.Inventory.Find(new(InventoryListType.Main, 65)) == before.Inventory.Find(new(InventoryListType.Main, 65))! with
                    { Core = before.Inventory.Find(new(InventoryListType.Main, 65))!.Core with { Value = 2 } } &&
                after.Inventory.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Effect &&
                    row.ItemId == EpicRoadRecoveryTests.Potion && row.UntilMilliseconds == usedAt + 30000) &&
                SameServiceCharacter(f.Session.Selected!, ReopenServiceCharacter(f)),
                "727原15B请求经真实Handler成功扣同UID一瓶，完整12B ACK先交本地药效且不重复发送源14");
            string unchanged = ServiceItemAssets(f);
            var next = (byte[])body.Clone(); next[3] = 2;
            wire = EpicItemWire(f, towns, characters, resources, 44, next);
            That(wire.Result == DfoServer.Network.Protocol.CommandResult.Handled && wire.Frames.Count == 1 &&
                wire.Frames[0].Kind == 1 && wire.Frames[0].Opcode == 44 &&
                wire.Frames[0].Body.AsSpan().SequenceEqual(Convert.FromHexString("007B0000020000001F04351D")) &&
                ServiceItemAssets(f) == unchanged, "727新token冷却拒绝只回失败ACK，完整源/效果/资产不变");
            wire = EpicItemWire(f, towns, characters, resources, 44, body);
            That(wire.Result == DfoServer.Network.Protocol.CommandResult.CloseConnection && wire.Frames.Count == 0 &&
                ServiceItemAssets(f) == unchanged, "727成功token完整重放按原合同关闭且无ACK、不二次扣件或续药效");
        }

        Console.WriteLine($"PASS {Count - checks} 星河727原资源禁图、真实普通实例、Handler/Sender、冷却与回城断言（非socket TCP或实机）");

        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        void Enter(Fixture f)
        {
            f.Select();
            That(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "727经真实城镇入口旅行");
            f.Session.BeginDungeonSelection();
            That(f.Session.EnterFormalDungeon(3, 0, 0, global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive),
                global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive)) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null, "727建立真实已加载普通DGN3，不用诊断状态代替");
        }
    }

    public static void RunEpicRoadRecoveryRaid(GameSession session, CharacterRepository repository, AccountOwners.Lease owner, ItemResources resources)
    {
        int checks = Count;
        var failures = new List<string>();
        var room = session.CurrentFormalDungeonScene();
        That(session.Phase == SessionPhase.Dungeon && room is { Entry.RaidEntry: not null } &&
            EpicRoadRecoveryTests.OriginalExclusions.Contains(room.Entry.DungeonId),
            "727团本回归使用真实已加载RaidEntry，且当前图确属原31表，不按频道或图名猜团队");
        That(resources.RecoveryConsumables.TryGet(EpicRoadRecoveryTests.Potion, out var original), "团本用例读取727原资源定义");
        var seeded = session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            [new(EpicRoadRecoveryTests.Potion, 3), new(1108, 3)], resources.Items, resources.GoldLimits, resources.EpicPieces), requireTown: false);
        That(seeded.Accepted, "隔离团本夹具准备原727及无raid禁用的1108真实源");
        var clock = new FakeClock();
        foreach (bool raidOnly in new[] { false, true })
        {
            var definition = raidOnly ? original with { ExcludedDungeons = ImmutableHashSet<uint>.Empty } : original;
            var service = new InventoryConsumableUseService(new RecoveryConsumableCatalog([definition, ConsumableUseTests.Hp]), clock);
            var before = Read();
            var source = before.Inventory.Items.Single(item => item.Core.ItemId == EpicRoadRecoveryTests.Potion);
            var denied = session.UseConsumable(new(source.Position, raidOnly ? 2U : 1U, source.Core.ItemId, 0), service);
            if (!(denied.Outcome == ConsumableUseOutcome.Rejected && denied.Inventory is null &&
                JsonSerializer.Serialize(before) == JsonSerializer.Serialize(Read()) &&
                JsonSerializer.Serialize(before) == JsonSerializer.Serialize(session.Selected)))
                failures.Add(raidOnly ? "清空显式禁图后仍须按原raid标签拒绝，完整资产不变" : "原727在真实禁图/团本必须拒绝且完整资产不变");
            // 首次RED可能实际误消费；前进原私有时钟并换token，防止冷却或重放歧义伪装第二个限制成立。
            clock.Advance(TimeSpan.FromSeconds(31));
        }
        var controlBefore = Read();
        var control = controlBefore.Inventory.Items.Single(item => item.Core.ItemId == 1108);
        var potionSource = controlBefore.Inventory.Items.Single(item => item.Core.ItemId == EpicRoadRecoveryTests.Potion);
        var controlService = new InventoryConsumableUseService(new RecoveryConsumableCatalog([ConsumableUseTests.Hp]), clock);
        var accepted = session.UseConsumable(new(control.Position, 3, control.Core.ItemId, 0), controlService);
        if (!(accepted.Outcome == ConsumableUseOutcome.Committed &&
            Read().Inventory.Find(control.Position)!.Core.Count == control.Core.Count - 1 && Read().Inventory.Find(potionSource.Position) == potionSource))
            failures.Add("同一真实团本未声明raid禁用的1108仍可消费一次，727拒绝不能靠通用团本门禁误通过");
        That(failures.Count == 0, "星河727真实团本资源限制：" + string.Join(" | ", failures));
        Console.WriteLine($"PASS {Count - checks} 星河727真实RaidEntry、禁图/raidOnly与普通药对照断言（Session/SQLite，非CMD44网络或实机）");
        DfoServer.Game.Characters.Character Read() => repository.LoadCharacter(owner, session.Selected!.Slot);
    }
}
