using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.TagEvildom;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 账号刷新地下城入场次数：覆盖角色日/周次数、账号次数（日键与任意周起点）、冒险团面板、魔兽、强袭、
// 魔界塔当日入场、悲叹塔账号额度与角色当日通关锁、祭坛/绝望当日入场回执、卢克团本周通关、再战魔界周次数。
// 旧周期行、其他账号、次元裂缝的每日奖励进度、普通卢克的当日楼层顺序保持原样。
public static class WeeklyEntryResetTests
{
    public static void RunCore()
    {
        int before = Count;
        using var f = new MailboxPersistenceTests.Fixture();
        f.OtherOwner.Dispose();
        f.ReceiverOwner.Dispose();

        var now = DateTimeOffset.UtcNow;
        int panelWeek = WeeklyDungeonPolicy.WeekKey(now);
        var period = DungeonIncountPolicy.Period(now);
        int beastDay = ShopPurchaseCount.Today(now);
        int assaultDay = AssaultModeEntryPolicy.DayKey(DateTimeOffset.Now);
        var accountPeriods = DungeonIncountPolicy.CurrentAccountPeriods(now);
        int otherWeekday = accountPeriods[2];
        int griefDay = QuestCycle.Day(now);
        var tag = TagEvildomPeriod.At(now);
        long otherId = f.Other.Id;
        long receiverId = f.Receiver.Id;
        long senderId = f.Sender.Id;
        long Scalar(string sql) => Convert.ToInt64(f.Query(sql));

        f.Execute($"""
            INSERT INTO character_weekly_dungeon(character_id,account,week,slot,used) VALUES
              ({otherId},'other',{panelWeek},0,3),
              ({otherId},'other',20180215,1,4),
              ({receiverId},'receiver',{panelWeek},0,5);
            INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used) VALUES
              ({otherId},'other',11006,{period.Day},2,{period.Week},6),
              ({otherId},'other',11007,20180215,1,20180215,7),
              ({receiverId},'receiver',11006,{period.Day},2,{period.Week},8);
            INSERT INTO account_dungeon_incount(account,dungeon,period,used) VALUES
              ('other',400000016,{period.Week},9),
              ('other',5340,{period.Day},4),
              ('other',400000017,{otherWeekday},2),
              ('other',400000018,20180215,3),
              ('receiver',400000016,{period.Week},5);
            INSERT INTO character_beast_dungeon_entries(character_id,account,day,dungeon_id,used) VALUES
              ({otherId},'other',{beastDay},310,1),
              ({otherId},'other',{beastDay - 1},311,2),
              ({receiverId},'receiver',{beastDay},310,1);
            INSERT INTO character_assault_daily(character_id,account,day,clear_count,hidden_count) VALUES
              ({otherId},'other',{assaultDay},2,1),
              ({senderId},'sender',{assaultDay - 1},2,0),
              ({receiverId},'receiver',{assaultDay},1,1);
            INSERT INTO character_crack_daily(character_id,account,day,clear_count,daily_reward) VALUES
              ({otherId},'other',{assaultDay},2,1);
            INSERT INTO account_dazzlement_tower_bindings(account,slot,week,character_id,last_entry_day) VALUES
              ('other',1,{panelWeek},{otherId},{beastDay}),
              ('other',2,20180215,NULL,0),
              ('receiver',1,{panelWeek},{receiverId},{beastDay});
            INSERT INTO account_grief_tower(account,game_day,used) VALUES
              ('other',{griefDay},2),
              ('receiver',{griefDay},1);
            INSERT INTO character_grief_tower(character_id,account,game_day,cleared,layer,instance_id,task_attempt) VALUES
              ({otherId},'other',{griefDay},1,5,'11111111111111111111111111111111',1),
              ({receiverId},'receiver',{griefDay},1,3,'22222222222222222222222222222222',1);
            INSERT INTO dungeon_special_entry_receipts(character_id,account,instance_id,task_attempt,service_day,kind,dungeon_id,layer,gold,revision) VALUES
              ({otherId},'other','33333333333333333333333333333333',1,{beastDay},1,11006,0,0,1),
              ({otherId},'other','44444444444444444444444444444444',2,{beastDay},3,11008,1,0,1),
              ({receiverId},'receiver','55555555555555555555555555555555',1,{beastDay},1,11006,0,0,1);
            INSERT INTO dungeon_despair_progress(character_id,account,next_layer,last_clear_day,clear_count) VALUES
              ({otherId},'other',2,{beastDay},1),
              ({receiverId},'receiver',2,{beastDay},1);
            INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,phase1_event,phase1_anchor,phase1_receipt,clear_week) VALUES
              ({otherId},'other','66666666666666666666666666666666',1,'fixture',0,2,'e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1',0,json_object(),{panelWeek}),
              ({otherId},'other','77777777777777777777777777777777',1,'fixture',0,2,'e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2',0,json_object(),20180215),
              ({otherId},'other','88888888888888888888888888888888',1,'fixture',0,0,NULL,NULL,NULL,NULL),
              ({receiverId},'receiver','99999999999999999999999999999999',1,'fixture',0,2,'e3e3e3e3e3e3e3e3e3e3e3e3e3e3e3e3',0,json_object(),{panelWeek});
            INSERT INTO account_tag_evildom_clearances(receipt,account,day,week,first,second) VALUES
              ('r-other-this','other',{tag.Day},{tag.Week},1,2),
              ('r-other-old','other',20180215,20180212,1,2),
              ('r-receiver','receiver',{tag.Day},{tag.Week},1,2);
            INSERT INTO account_tag_evildom_challenges(account,day,week,team_index,maze_ordinal,dungeon_id,instance_id,first,second,admitted_at,sequence,state) VALUES
              ('other',{tag.Day - 1},{tag.Week},0,0,9608,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1,2,0,1,0),
              ('other',{tag.Day},{tag.Week},1,1,9608,'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',1,2,0,2,1),
              ('receiver',{tag.Day - 1},{tag.Week},0,0,9608,'cccccccccccccccccccccccccccccccc',1,2,0,1,0);
            """);

        string answer = DevelopmentGm.Answer("account other weekly-entry-reset", "sender", f.Repository, f.Owners, _ => null);
        That(answer.StartsWith("OK ", StringComparison.Ordinal), "入场次数刷新命令成功: " + answer);
        using var document = JsonDocument.Parse(answer[3..answer.IndexOf(" 已清零", StringComparison.Ordinal)]);
        var cleared = document.RootElement;
        That(cleared.GetProperty("Account").GetString() == "other" &&
            cleared.GetProperty("Week").GetInt32() == panelWeek &&
            cleared.GetProperty("PanelRows").GetInt32() == 1 &&
            cleared.GetProperty("CharacterDailyRows").GetInt32() == 1 &&
            cleared.GetProperty("CharacterWeeklyRows").GetInt32() == 1 &&
            cleared.GetProperty("AccountRows").GetInt32() == 3 &&
            cleared.GetProperty("BeastRows").GetInt32() == 1 &&
            cleared.GetProperty("AssaultRows").GetInt32() == 1,
            "结果按存储分类只统计本账号当前周期的行");

        That(f.Query($"SELECT count(*) FROM character_weekly_dungeon WHERE account='other' AND week={panelWeek}") is 0L &&
            f.Query($"SELECT used FROM character_weekly_dungeon WHERE account='other' AND week=20180215") is 4L &&
            f.Query($"SELECT used FROM character_weekly_dungeon WHERE account='receiver' AND week={panelWeek}") is 5L,
            "冒险团面板只删本账号本周，旧周和其他账号保留");
        That(f.Query($"SELECT daily_used FROM character_dungeon_incount WHERE account='other' AND dungeon=11006") is 0L &&
            f.Query($"SELECT weekly_used FROM character_dungeon_incount WHERE account='other' AND dungeon=11006") is 0L &&
            f.Query($"SELECT daily_used FROM character_dungeon_incount WHERE account='other' AND dungeon=11007") is 1L &&
            f.Query($"SELECT weekly_used FROM character_dungeon_incount WHERE account='other' AND dungeon=11007") is 7L &&
            f.Query($"SELECT weekly_used FROM character_dungeon_incount WHERE account='receiver' AND dungeon=11006") is 8L,
            "角色日/周次数清零，旧周和其他账号不动");
        That(f.Query($"SELECT used FROM account_dungeon_incount WHERE account='other' AND dungeon=400000016") is 0L &&
            f.Query($"SELECT used FROM account_dungeon_incount WHERE account='other' AND dungeon=5340") is 0L &&
            f.Query($"SELECT used FROM account_dungeon_incount WHERE account='other' AND dungeon=400000017") is 0L &&
            f.Query($"SELECT used FROM account_dungeon_incount WHERE account='other' AND dungeon=400000018") is 3L &&
            f.Query($"SELECT used FROM account_dungeon_incount WHERE account='receiver' AND dungeon=400000016") is 5L,
            "账号次数清日键与任意周起点，旧周期和其他账号不动");
        That(f.Query($"SELECT count(*) FROM character_beast_dungeon_entries WHERE account='other' AND day={beastDay}") is 0L &&
            f.Query($"SELECT used FROM character_beast_dungeon_entries WHERE account='other' AND day={beastDay - 1}") is 2L &&
            f.Query($"SELECT used FROM character_beast_dungeon_entries WHERE account='receiver' AND day={beastDay}") is 1L,
            "魔兽只删本账号当日，前一日和其他账号保留");
        That(f.Query($"SELECT clear_count FROM character_assault_daily WHERE account='other' AND day={assaultDay}") is 0L &&
            f.Query($"SELECT hidden_count FROM character_assault_daily WHERE account='other' AND day={assaultDay}") is 0L &&
            f.Query($"SELECT clear_count FROM character_assault_daily WHERE account='sender' AND day={assaultDay - 1}") is 2L &&
            f.Query($"SELECT clear_count FROM character_assault_daily WHERE account='receiver' AND day={assaultDay}") is 1L,
            "强袭只清本账号当日次数，前一日和其他账号保留");
        That(f.Query($"SELECT clear_count FROM character_crack_daily WHERE account='other' AND day={assaultDay}") is 2L &&
            f.Query($"SELECT daily_reward FROM character_crack_daily WHERE account='other' AND day={assaultDay}") is 1L,
            "次元裂缝的每日奖励进度不在入场次数范围");

        That(cleared.GetProperty("DazzlementRows").GetInt32() == 1 &&
            cleared.GetProperty("GriefAccountRows").GetInt32() == 1 &&
            cleared.GetProperty("GriefCharacterRows").GetInt32() == 1 &&
            cleared.GetProperty("SpecialEntryRows").GetInt32() == 2 &&
            cleared.GetProperty("DespairRows").GetInt32() == 1 &&
            cleared.GetProperty("RaidRows").GetInt32() == 1 &&
            cleared.GetProperty("TagClearanceRows").GetInt32() == 1 &&
            cleared.GetProperty("TagChallengeRows").GetInt32() == 1,
            "新增覆盖的玩法按存储分类只统计本账号当前周期的行");
        That(Scalar($"SELECT last_entry_day FROM account_dazzlement_tower_bindings WHERE account='other' AND slot=1") == 0 &&
            Scalar($"SELECT last_entry_day FROM account_dazzlement_tower_bindings WHERE account='other' AND slot=2") == 0 &&
            Scalar($"SELECT last_entry_day FROM account_dazzlement_tower_bindings WHERE account='receiver'") == beastDay,
            "魔界塔清本账号本周绑定行的当日入场，旧周与其他账号不动");
        That(Scalar($"SELECT game_day FROM account_grief_tower WHERE account='other'") == 1 &&
            Scalar($"SELECT used FROM account_grief_tower WHERE account='other'") == 0 &&
            Scalar($"SELECT game_day FROM character_grief_tower WHERE character_id={otherId}") == 1 &&
            Scalar($"SELECT cleared FROM character_grief_tower WHERE character_id={otherId}") == 0 &&
            Scalar($"SELECT layer FROM character_grief_tower WHERE character_id={otherId}") == 5 &&
            Scalar($"SELECT used FROM account_grief_tower WHERE account='receiver'") == 1,
            "悲叹塔清账号日额度与角色当日通关锁，楼层进度与其他账号不动");
        That(Scalar($"SELECT count(*) FROM dungeon_special_entry_receipts WHERE account='other' AND service_day={beastDay}") == 0 &&
            Scalar($"SELECT count(*) FROM dungeon_special_entry_receipts WHERE account='other' AND service_day=1") == 2 &&
            Scalar($"SELECT count(*) FROM dungeon_special_entry_receipts WHERE account='receiver' AND service_day={beastDay}") == 1,
            "祭坛/绝望当日入场回执挪出当日计数，回执行本身与其他账号保留");
        That(Scalar($"SELECT last_clear_day FROM dungeon_despair_progress WHERE character_id={otherId}") == 0 &&
            Scalar($"SELECT next_layer FROM dungeon_despair_progress WHERE character_id={otherId}") == 2 &&
            Scalar($"SELECT last_clear_day FROM dungeon_despair_progress WHERE character_id={receiverId}") == beastDay,
            "绝望清当日通关日，楼层进度与其他账号不动");
        That(Scalar($"SELECT clear_week FROM character_raid_runs WHERE instance_id='66666666666666666666666666666666'") == 1 &&
            Scalar($"SELECT clear_week FROM character_raid_runs WHERE instance_id='77777777777777777777777777777777'") == 20180215 &&
            Scalar($"SELECT state FROM character_raid_runs WHERE instance_id='88888888888888888888888888888888'") == 0 &&
            Scalar($"SELECT count(*) FROM character_raid_runs WHERE account='other'") == 3 &&
            Scalar($"SELECT clear_week FROM character_raid_runs WHERE instance_id='99999999999999999999999999999999'") == panelWeek,
            "卢克本周通关挪到旧周，行本身、在途副本与其他账号保留");
        That(Scalar($"SELECT day FROM account_tag_evildom_clearances WHERE receipt='r-other-this'") == 1 &&
            Scalar($"SELECT week FROM account_tag_evildom_clearances WHERE receipt='r-other-this'") == 1 &&
            Scalar($"SELECT week FROM account_tag_evildom_clearances WHERE receipt='r-other-old'") == 20180212 &&
            Scalar($"SELECT week FROM account_tag_evildom_clearances WHERE receipt='r-receiver'") == tag.Week,
            "再战魔界周通关回执只把本账号本周的挪到第1周，旧周与其他账号不动");
        That(Scalar($"SELECT count(*) FROM account_tag_evildom_challenges WHERE account='other'") == 1 &&
            Scalar($"SELECT state FROM account_tag_evildom_challenges WHERE account='other'") == 1 &&
            Scalar($"SELECT count(*) FROM account_tag_evildom_challenges WHERE account='receiver'") == 1,
            "再战魔界跨日未完成尝试删掉，已明确退场的与其他账号保留");

        string again = DevelopmentGm.Answer("weekly-entry-reset", "other", f.Repository, f.Owners, _ => null);
        That(again.StartsWith("OK ", StringComparison.Ordinal) &&
            f.Query($"SELECT count(*) FROM character_weekly_dungeon WHERE account='other' AND week=20180215") is 1L &&
            f.Query($"SELECT used FROM account_dungeon_incount WHERE account='other' AND dungeon=400000018") is 3L,
            "再刷一次仍然成功，旧周期行还在");
        Console.WriteLine($"PASS {Count - before} 地下城入场次数刷新核心断言");
    }
}
