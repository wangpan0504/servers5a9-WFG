using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ConsumableStateTests
{
    public static void RunCore()
    {
        const long now = 1900000000000;
        var potion = new RecoveryConsumableDefinition(10087825, 1, 1800000, 5)
            { EffectMilliseconds = 1800000, EffectMaintenance = 7, CooltimeMaintenance = 7 };
        var before = InventorySnapshot.Empty with { StaminaUntil = now / 1000 + 600 };
        var active = ConsumableStatePolicy.Activate(before, potion, now)!;
        That(active.StaminaUntil == 0 && active.ConsumableStates.Length == 2 &&
            active.ConsumableStates.All(row => row.UntilMilliseconds == now + 1800000),
            "防虚弱效果和冷却都按资源毫秒起算，使用时恢复已有虚弱，不改变其他资产");
        Bytes(ConsumableStatePackets.Restore(active, ConsumableStateKind.Effect, now + 1000)!,
            [1,0x91,0xED,0x99,0,0x07,0x07,0,0], "174独立预期：count、itemId、1799剩余秒，不发绝对截止或毫秒");
        Bytes(ConsumableStatePackets.Restore(active, ConsumableStateKind.Cooldown, now + 1000)!,
            [1,0x91,0xED,0x99,0,0x07,0x07,0,0], "172沿原生独立冷却列表，不混用效果和冷却计时");
        That(ConsumableStatePackets.Restore(active, ConsumableStateKind.Effect, now + 1800000) is null &&
            ConsumableStatePackets.Restore(active, ConsumableStateKind.Effect, now + 1799500) is null,
            "过期及不足一秒的状态不恢复，不能把0秒传给客户端而重启完整药效");
        foreach (var boundary in new[] { ConsumableMaintenance.Death, ConsumableMaintenance.Town, ConsumableMaintenance.Reselect })
            That(ReferenceEquals(ConsumableStatePolicy.CrossBoundary(active, boundary, now + 1000), active),
                "保留位均开启的药剂跨死亡、回城、重选不续期、不重复扣源");
        var transient = active with { ConsumableStates = active.ConsumableStates.Select(row => row with { Maintenance = 0 }).ToImmutableArray() };
        foreach (var boundary in new[] { ConsumableMaintenance.Death, ConsumableMaintenance.Town, ConsumableMaintenance.Reselect })
            That(ConsumableStatePolicy.CrossBoundary(transient, boundary, now).ConsumableStates.IsEmpty,
                "未声明保留的临时效果和冷却在对应边界清除，不被持久行重新复活");
        var shorter = ConsumableStatePolicy.Activate(active, potion with { EffectMilliseconds = 60000, CoolTime = 60000 }, now + 1000)!;
        That(shorter.ConsumableStates.SequenceEqual(active.ConsumableStates), "重复同ID不缩短已有持续效果，也不按剩余时间叠加期限");
        var blessing = new RecoveryConsumableDefinition(2600020, 1, 1200000, -2600020)
            { EffectMilliseconds = 1200000, RevivalBlessing = true };
        var firstBlessing = ConsumableStatePolicy.Activate(InventorySnapshot.Empty, blessing, now)!;
        var renewedBlessing = ConsumableStatePolicy.Activate(firstBlessing,
            blessing with { ItemId = 2680783, CoolGroup = -2680783 }, now + 1000)!;
        That(renewedBlessing.ConsumableStates is [{ ItemId: 2680783, UntilMilliseconds: now + 1201000 }] &&
            ConsumableStatePolicy.CrossBoundary(renewedBlessing, ConsumableMaintenance.Town, now + 1000).ConsumableStates.IsEmpty,
            "不同神圣祝福覆盖同一二十分钟资格，不累计次数，回城无条件失效");
        var other = ConsumableStatePolicy.Activate(active, potion with { ItemId = 1113, CoolGroup = 1, CoolTime = 10000 }, now)!;
        That(other.ConsumableStates.Count(row => row.Kind == ConsumableStateKind.Cooldown) == 2,
            "不同冷却组保持独立，不用最长持续时间封锁全部药剂");
        var slots = Enumerable.Range(1, 255).Select(id => new ConsumableState(ConsumableStateKind.Effect, id, now + 60000, 7, id)).ToImmutableArray();
        That(ConsumableStatePolicy.Activate(InventorySnapshot.Empty with { ConsumableStates = slots }, potion, now) is null,
            "协议容量满时在扣物前拒绝，不能截断已有合法药效");
        var time = DateTimeOffset.FromUnixTimeMilliseconds(now);
        var map = new DungeonMap("boss", new(0, 0), 1, "map/potion-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "BB", [new(0, 0)], [new(0, 0)], [map], []);
        var entry = new DungeonEntryPlan(1, 1, 3, 0, byte.MaxValue, 0, maze, map, map);
        var actor = new Character(1, 0, "Potion", 1, new(1, 0, 0, 0), null) { Level = 40, Inventory = active };
        That(StaminaRecoveryService.ApplyDungeonRetreat(actor, entry, SinglePlayerDungeonPhase.Dungeon, time).StaminaUntil == 0 &&
            StaminaRecoveryService.ApplyDungeonRetreat(actor, entry, SinglePlayerDungeonPhase.Dungeon, time.AddMinutes(30)).StaminaUntil > 0,
            "有效防虚弱药剂在退图惩罚触发后立即恢复；恰到期恢复普通惩罚，不赋予黑钻或网吧身份");
        Bytes(DfoServer.Network.Builders.DungeonPackets.CharacterLife(0x1234, (byte)2), [0x34,0x12,2,0],
            "神圣祝福使用本版32的状态2与尾字节，不伪造扣币应答或使用普通满血复活");
        Bytes(ConsumableStatePackets.Grant(7796, 3600), [1,0x74,0x1E,0,0,0x10,0x0E,0,0],
            "本版257按dummy物品ID及剩余秒给药效，不发送实物奖励");
        Bytes(ConsumableStatePackets.ItemEffect(0x1234,7796), [0x34,0x12,0x74,0x1E,0,0], "本版260按源角色和dummy物品播放施放表现");
        var caster = actor with { Id = 2, Level = 70 };
        var nearby = actor with { Level = 69, Position = actor.Position with { X = 300, Y = 300 } };
        That(ConsumableStatePolicy.AllowsTownGift(caster, nearby) && !ConsumableStatePolicy.AllowsTownGift(caster, caster) &&
            !ConsumableStatePolicy.AllowsTownGift(caster, nearby with { Level = 70 }) &&
            !ConsumableStatePolicy.AllowsTownGift(caster, nearby with { Position = nearby.Position with { X = 301 } }),
            "高人的恩赐排除自身、70级及超出300范围者，频道与地图隔离继续由城镇可见性负责");
    }
}
