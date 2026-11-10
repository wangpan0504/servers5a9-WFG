using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Settings;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Development;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GmAccountLimitsTests
{
    public static void RunCore()
    {
        int before = Count;
        FatiguePersistence();
        CombinedReset();
        Console.WriteLine($"PASS GmAccountLimitsTests ({Count - before} checks)");
    }

    static void FatiguePersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-account-fatigue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountFatigueStore(root);
            var settings = new GameDebugSettings(initialFatigueAccounts: store.Read(), persistFatigue: store.Write);
            That(!settings.InfiniteFatigueFor("first") && !settings.InfiniteFatigueFor("second"), "无配置时每个账号默认关闭无限疲劳");
            settings.SetInfiniteFatigue("first", true);
            That(settings.InfiniteFatigueFor("first") && !settings.InfiniteFatigueFor("second"), "只开启指定账号，不影响另一账号");
            settings = new GameDebugSettings(initialFatigueAccounts: store.Read(), persistFatigue: store.Write);
            That(settings.InfiniteFatigueFor("first") && !settings.InfiniteFatigueFor("second"), "服务重建后保持各账号独立选择");
            settings.SetInfiniteFatigue("first", false);
            That(!new GameDebugSettings(initialFatigueAccounts: store.Read()).InfiniteFatigueFor("first"), "关闭也持久保存，不回到默认开");
            var broken = new GameDebugSettings(persistFatigue: _ => throw new IOException("injected-save-failure"));
            Reject<IOException>(() => broken.SetInfiniteFatigue("first", true), "保存失败须报告失败");
            That(!broken.InfiniteFatigueFor("first") && !broken.InfiniteFatigueFor("second"), "保存失败不能发布未持久化状态");
            settings.SetInfiniteFatigue("first", true);
            settings.SetInfiniteFatigue("second", true);
            settings.SetInfiniteFatigue("first", false);
            That(store.Read().SetEquals(["second"]), "关闭一个账号保留另一账号已保存的开关");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    static void CombinedReset()
    {
        using var f = RaidQuotaEpochTests.CreateFixture();
        var now = DateTimeOffset.UtcNow;
        int day = ShopPurchaseCount.Today(now), week = DungeonIncountPolicy.Period(now).Week;
        int entryDay = DungeonIncountPolicy.Period(now).Day;
        var tag = DfoServer.Game.TagEvildom.TagEvildomPeriod.At(now);
        int crackDay = AssaultModeEntryPolicy.DayKey(now.ToLocalTime());
        f.Execute($"""
            INSERT INTO character_dungeon_incount VALUES({f.Other.Id},'other',11006,{entryDay},2,{week},3),
              ({f.Receiver.Id},'receiver',11006,{entryDay},4,{week},5);
            INSERT INTO character_crack_daily VALUES({f.Other.Id},'other',{crackDay},2,1),
              ({f.Receiver.Id},'receiver',{crackDay},3,1);
            INSERT INTO account_endless_adventure_days VALUES('other',{day},200),('receiver',{day},200);
            INSERT INTO account_tag_evildom_clearances(receipt,account,day,week,first,second)
            VALUES('combined-history','other',{tag.Day},{tag.Week},1,2);
            """);
        string assets = System.Text.Json.JsonSerializer.Serialize(f.Repository.LoadCharacter(f.OtherOwner, 0));
        // 在奖励刷新已执行后故意让入场刷新失败，验证整个事务回滚。
        f.Execute("CREATE TRIGGER reject_combined_reset BEFORE UPDATE ON character_dungeon_incount BEGIN SELECT RAISE(ABORT,'combined-late-failure'); END;");
        string saved = f.Snapshot();
        Reject<SqliteException>(() => f.Repository.ResetDungeonLimits(f.OtherOwner), "入场刷新晚期失败不能留下半次奖励刷新");
        That(f.Snapshot() == saved, "组合刷新失败回滚所有表与账号资格");
        f.Execute("DROP TRIGGER reject_combined_reset;");
        var result = f.Repository.ResetDungeonLimits(f.OtherOwner);
        That(result.Entries.Account == "other" && result.Rewards.Account == "other" && result.CrackDailyRows == 1 && result.EndlessAdventureRows == 1,
            "组合回执同时包含当前账号入场、奖励与活动额度");
        That(f.Query("SELECT daily_used+weekly_used FROM character_dungeon_incount WHERE account='other'") is 0L &&
            f.Query("SELECT clear_count+daily_reward FROM character_crack_daily WHERE account='other'") is 0L &&
            f.Query("SELECT earned FROM account_endless_adventure_days WHERE account='other'") is 0L, "当前账号入场和活动奖励计数恢复");
        That(f.Query("SELECT daily_used+weekly_used FROM character_dungeon_incount WHERE account='receiver'") is 9L &&
            f.Query("SELECT clear_count+daily_reward FROM character_crack_daily WHERE account='receiver'") is 4L &&
            f.Query("SELECT earned FROM account_endless_adventure_days WHERE account='receiver'") is 200L, "其他账号所有计数保持原样");
        That(f.Query($"SELECT count(*) FROM account_tag_evildom_clearances WHERE receipt='combined-history' AND week>{tag.Week} AND day>{tag.Day}") is 1L,
            "奖励资格退出当前周期但历史回执仍完整保留");
        That(System.Text.Json.JsonSerializer.Serialize(f.Repository.LoadCharacter(f.OtherOwner, 0)) == assets, "刷新额度不改角色资产、钱包或revision");
        saved = f.Snapshot();
        f.Repository.ResetDungeonLimits(f.OtherOwner);
        That(f.Snapshot() == saved, "重复刷新不制造资产、记录或额外世代");
        f.Integrity();
    }
}
