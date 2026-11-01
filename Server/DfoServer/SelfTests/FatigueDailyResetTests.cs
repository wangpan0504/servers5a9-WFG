using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FatigueDailyResetTests
{
    public static void RunCore()
    {
        int before = Count;
        var zone = TimeZoneInfo.CreateCustomTimeZone("fatigue-test", TimeSpan.FromHours(8), "test", "test");
        var now = DateTimeOffset.Parse("2030-05-01T21:59:59Z");
        int day = new DateOnly(2030, 5, 1).DayNumber;
        That(FatigueDailyReset.Day(now, zone) == day && FatigueDailyReset.Day(now.AddSeconds(1), zone) == day + 1 &&
            FatigueDailyReset.NextReset(now, zone) == now.AddSeconds(1), "疲劳以服务器本地06:00而非00:00分日");
        That(FatigueDailyReset.Day(now, TimeZoneInfo.Utc) == day &&
            FatigueDailyReset.NextReset(now, TimeZoneInfo.Utc) == DateTimeOffset.Parse("2030-05-02T06:00:00Z"), "服务日跟随服务器时区，不强制UTC加八");
        var utcBefore = DateTimeOffset.Parse("2030-05-02T05:59:59Z");
        That(FatigueDailyReset.Day(utcBefore, TimeZoneInfo.Utc) == day &&
            FatigueDailyReset.Day(utcBefore.AddSeconds(1), TimeZoneInfo.Utc) == day + 1 &&
            FatigueDailyReset.NextReset(utcBefore, TimeZoneInfo.Utc) == utcBefore.AddSeconds(1),
            "非东八区疲劳在本机六点跨日");
        That(DfoServer.Game.Inventory.Models.ShopPurchaseCount.Today(utcBefore) == day + 1 &&
            DfoServer.Game.Inventory.Models.ShopPurchaseCount.Today(utcBefore.AddSeconds(1)) == day + 1 &&
            DfoServer.Game.Inventory.Models.DailyBoosterUsage.NextReset(utcBefore) == DateTimeOffset.Parse("2030-05-02T22:00:00Z") &&
            DfoServer.Game.Quests.QuestCycle.Day(utcBefore) == 20300502 &&
            DfoServer.Game.Quests.QuestCycle.Day(utcBefore.AddSeconds(1)) == 20300502,
            "原有商店、礼盒及任务继续采用东八区日界，不随疲劳本机时区改变");
        var dst=TimeZoneInfo.CreateCustomTimeZone("reset-dst",TimeSpan.Zero,"test","standard","daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2026,1,1),new(2030,12,31),TimeSpan.FromHours(2),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new(1,1,1,6,0,0),3,1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new(1,1,1,7,0,0),11,1))]);
        That(FatigueDailyReset.NextReset(DateTimeOffset.Parse("2026-11-01T05:30:00Z"),dst)==DateTimeOffset.Parse("2026-11-01T06:00:00Z"),
            "夏令时回拨穿过六点后仍选择未来唤醒，不产生负延迟或再次发奖");
        That(FatigueDailyReset.NextReset(DateTimeOffset.Parse("2026-03-01T05:59:59Z"),dst)==DateTimeOffset.Parse("2026-03-01T06:00:00Z"),
            "不存在的本地六点取跳时后第一个有效时刻");
        var state = new CharacterFatigueState(150, 156, 23, 6, 11, 9) { ResetDay = day };
        var restored = FatigueDailyReset.Restore(state, day + 1, false);
        That(restored == state with { Used = 0, Remaining = 156, ResetDay = day + 1 }, "新日补满且保留未解释的疲劳附属字段");
        That(ReferenceEquals(FatigueDailyReset.Restore(restored, day + 1, false), restored) &&
            ReferenceEquals(FatigueDailyReset.Restore(restored, day, false), restored), "重复、重登及墙钟回拨不重复恢复");
        That(FatigueDailyReset.Restore(state, day + 1, true) is { Maximum: 188, Remaining: 188, Used: 0 } &&
            FatigueDailyReset.Restore(state.GrantBlackDiamondCapacity(), day + 1, false) is { Maximum: 156, Remaining: 156 },
            "按日切时真实黑钻资格选择156或188，不叠加重复32点");
        string root = Path.Combine(Path.GetTempPath(), "fatigue-day-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("fatigue-day", () => { });
            var character = repository.Create(owner, new("Daily", new(1, 0, 10, 10), null) { DungeonState = CharacterDungeonState.NewOrdinary });
            using var sql = new SqliteConnection("Data Source=" + path); sql.Open();
            void Execute(string text) { using var command = sql.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery(); }
            Execute($"UPDATE character_fatigue SET used=150,remaining=6,used_maximum=23,context_auxiliary=11,selection_auxiliary=9 WHERE character_id={character.Id}; UPDATE character_fatigue_reset_days SET service_day={day} WHERE character_id={character.Id}");
            character = repository.LoadCharacter(owner, character.Slot);
            Execute("CREATE TRIGGER fail_daily_reset AFTER UPDATE ON character_fatigue_reset_days BEGIN SELECT RAISE(ABORT,'day rollback'); END");
            Reject<SqliteException>(() => repository.ResetDailyFatigue(owner, character, day + 1, now.AddSeconds(1)), "日戳晚期写入失败");
            var rolled = repository.LoadCharacter(owner, character.Slot);
            That(rolled.Revision == character.Revision && rolled.Fatigue == character.Fatigue && rolled.Inventory.Gold == character.Inventory.Gold &&
                rolled.Inventory.Items.SequenceEqual(character.Inventory.Items) && rolled.Inventory.AccountWallet.Revision == character.Inventory.AccountWallet.Revision,
                "恢复余额、日戳、revision及原资产整笔回滚");
            Execute("DROP TRIGGER fail_daily_reset");
            var committed = repository.ResetDailyFatigue(owner, character, day + 1, now.AddSeconds(1));
            That(committed.Fatigue == restored && committed.Revision == character.Revision + 1, "真实SQL提交后同时返回新余额和服务日");
            Reject<InvalidOperationException>(() => repository.ResetDailyFatigue(owner, character, day + 1, now.AddSeconds(1)), "旧revision不能覆盖已恢复快照");
            var repeated = repository.ResetDailyFatigue(owner, committed, day + 1, now.AddSeconds(1));
            That(ReferenceEquals(repeated, committed) && new CharacterRepository(path).LoadCharacter(owner, character.Slot).Fatigue == restored,
                "同日纯零变化，重开库保留已恢复事实");
            repository.SetCardMembership(owner, character.Name, CardMembershipKind.BlackDiamond, now.AddDays(3).ToUnixTimeSeconds());
            Execute("UPDATE character_fatigue SET used=170,remaining=18");
            var premium = repository.LoadCharacter(owner, character.Slot);
            premium = repository.ResetDailyFatigue(owner, premium, day + 2, now.AddDays(1));
            That(premium.Fatigue is { Used: 0, Maximum: 188, Remaining: 188 }, "真实账号黑钻资格的次日恢复188");
            repository.SetCardMembership(owner, character.Name, CardMembershipKind.BlackDiamond, 0);
            premium = repository.LoadCharacter(owner, character.Slot);
            premium = repository.ResetDailyFatigue(owner, premium, day + 3, now.AddDays(2));
            That(premium.Fatigue is { Used: 0, Maximum: 156, Remaining: 156 }, "取消账号黑钻后日切只恢复156");
            Execute("DELETE FROM character_fatigue_reset_days; UPDATE character_fatigue SET used=155,remaining=1");
            var legacy = repository.LoadCharacter(owner, character.Slot);
            That(legacy.Fatigue is { ResetDay: 0 }, "未知旧日戳不猜测历史次数");
            var first = repository.ResetDailyFatigue(owner, legacy, day + 1, now.AddSeconds(1));
            That(first.Fatigue is { Used: 0, Remaining: 156 } && first.Fatigue.ResetDay == day + 1,
                "旧存档首次接入恢复本服务日一次，不逐日累加");
            Execute("PRAGMA foreign_keys=ON; DELETE FROM character_fatigue");
            using var check = sql.CreateCommand(); check.CommandText = "SELECT count(*) FROM character_fatigue_reset_days";
            That(check.ExecuteScalar() is 0L && repository.LoadCharacter(owner, character.Slot).Fatigue is null,
                "疲劳owner删除级联清理日戳，未知余额不自动伪造");
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 疲劳服务日、真实恢复事务、去重与回滚断言");
    }
}
