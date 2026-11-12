using System.Collections.Immutable;
using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FatigueBatteryTests
{
    public static void RunCore()
    {
        int start = Count;
        var catalog = new FatigueBatteryCatalog(20000, 0.1f, 30, 10, 156, 5, 1,
            Enumerable.Repeat((ushort)235, 100).ToImmutableArray());
        var state = CharacterFatigueState.NewOrdinary;
        That(FatigueBattery.Recharge(state, catalog, 30, 0, 1) is { BatteryCharging: 0, BatteryBuff: 31 },
            "156未用疲劳按等级30及5比1兑换31祝福，充能余数不重复转换");
        That(FatigueBattery.Recharge(state with { Used = 155, Remaining = 1 }, catalog, 10, 0, 1)
            is { BatteryCharging: 0, BatteryBuff: 0 }, "先截断整数除法，不制造不足1的充能");
        That(FatigueBattery.Recharge(state with { Used = 150, Remaining = 6 }, catalog, 30, 2, 2)
            is { BatteryCharging: 0, BatteryBuff: 38 }, "跨两服务日补一个完整未用日，再乘账号满级角色权重");
        That(FatigueBattery.Recharge(state with { BatteryCharging = 155 }, catalog, 30, 0, 1)
            is { BatteryCharging: 0, BatteryBuff: 62 }, "已有充能与本次新增合并后一次兑换");
        That(FatigueBattery.Recharge(state, catalog, 9, 0, 1) == state &&
            FatigueBattery.Recharge(state, catalog, 95, 0, 1) == state &&
            FatigueBattery.Recharge(state, catalog, 94, 100, 9999).BatteryBuff == 235,
            "最低等级和满级排除，长离线及多满级角色不突破资源上限");
        var charged = state with { BatteryBuff = 3, BatteryCharging = 17 };
        That(charged.ConsumeAtEntry(2).BatteryBuff == 1 && charged.ConsumeOrdinaryRoom(8).BatteryBuff == 0 &&
            charged.GrantBlackDiamondCapacity().BatteryBuff == 3,
            "入场和房间扣点独立消耗祝福且不负数，黑钻不能复制电量");
        var reward = FatigueBattery.Apply(new(101, []), 101, true);
        That(reward.Total == 131 && reward.Amount(ExperienceBonusSource.FatigueBuff) == 30 &&
            FatigueBattery.Apply(new(101, []), 101, false).Total == 101,
            "真实基础杀怪经验整数30%，无资格不发放");
        Reject<InvalidDataException>(() => FatigueBattery.Apply(reward, 101, true), "重复电池来源拒绝");
        var packet = FatiguePackets.Current(charged);
        That(BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)) == 3 &&
            BinaryPrimitives.ReadUInt16LittleEndian(TownPackets.Select(new(1, 0, "Battery", 0, new(1,0,1,1), null)
                { Fatigue = charged }).AsSpan(228)) == 3, "NOTI36及ACK4独立投影真实祝福，不复制156剩余疲劳");
        Console.WriteLine($"PASS {Count - start} 疲劳蓄电池公式、门限、扣点与协议断言");
    }

    public static void RunResources(TcpTestResources resources)
    {
        int start = Count;
        var catalog = resources.Growth.FatigueBattery;
        That(catalog is { ChargingLimit: 20000, MinimumLevel: 10, ActivationMinimum: 156,
            LevelConstant: 30, ChargingPerBuff: 5, BuffPerCharging: 1 } && catalog.BuffLimit(60) == 235,
            "最终累计资源独立读取电池常数，30是等级除数而非经验来源");
        string root = Path.Combine(Path.GetTempPath(), "fatigue-battery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path, growthCatalog: resources.Growth);
            using var owner = new AccountOwners().Acquire("battery", () => { });
            var character = repository.Create(owner, new("Battery", resources.Towns.Room, resources.Towns.InitialReturn)
                { DungeonState = CharacterDungeonState.NewOrdinary });
            using var db = new SqliteConnection("Data Source=" + path); db.Open();
            void Sql(string text) { using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery(); }
            // 已发布207升级不得把原剩余疲劳解释为旧电量。
            Sql("DROP TABLE character_fatigue_battery; PRAGMA user_version=207");
            repository = new CharacterRepository(path, growthCatalog: resources.Growth);
            character = repository.LoadCharacter(owner, character.Slot);
            That(character.Fatigue is { Remaining: 156, BatteryBuff: 0, BatteryCharging: 0 }, "207存量升级保留疲劳且零电池");
            int day = character.Fatigue!.ResetDay;
            Sql("UPDATE characters SET level=60; UPDATE character_fatigue SET used=56,remaining=100");
            character = repository.LoadCharacter(owner, character.Slot);
            Sql("CREATE TRIGGER battery_rollback AFTER INSERT ON character_fatigue_battery BEGIN SELECT RAISE(ABORT,'battery rollback'); END");
            Reject<SqliteException>(() => repository.ResetDailyFatigue(owner, character, day + 1, DateTimeOffset.UtcNow.AddDays(1)),
                "电池最后一步写入失败");
            var rolledBack = repository.LoadCharacter(owner, character.Slot);
            That(rolledBack.Revision == character.Revision && rolledBack.Fatigue == character.Fatigue &&
                rolledBack.Inventory.Gold == character.Inventory.Gold, "电池、普通疲劳、日戳及revision整笔回滚");
            Sql("DROP TRIGGER battery_rollback");
            var result = repository.ResetDailyFatigue(owner, character, day + 1, DateTimeOffset.UtcNow.AddDays(1));
            That(result.Fatigue is { Remaining: 156, BatteryCharging: 0, BatteryBuff: 40 } &&
                result.Revision == character.Revision + 1, "真实日切100乘60除30再除5得到40电量");
            That(ReferenceEquals(repository.ResetDailyFatigue(owner, result, day + 1, DateTimeOffset.UtcNow.AddDays(1)), result) &&
                new CharacterRepository(path, growthCatalog: resources.Growth).LoadCharacter(owner, result.Slot).Fatigue == result.Fatigue,
                "同日重放不充电，重开库保留真实电量");
            Reject<InvalidOperationException>(() => repository.ResetDailyFatigue(owner, character, day + 1, DateTimeOffset.UtcNow.AddDays(1)),
                "旧revision不能再次充电");
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - start} 蓄电池资源、207升级、充电事务与回滚断言");
    }
}
