using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class TournamentSettlementTests
{
    public static void RunResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        VerifyResourceExperiencePolicies(pvf);
        VerifyResourceRounds(pvf);
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var skills = SelfTestCatalogs.SkillLearning(pvf);
        var growth = SelfTestCatalogs.Growth(pvf);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var clear = SelfTestCatalogs.ClearExperience(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var policy = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            items.Items, items.GoldLimits, items.EpicPieces, _ => 0);
        var blue = dungeons.Get(121);
        That(blue.TournamentRewards is { GoldRate: 16 } rules && rules.Experience[4] == 500000 &&
            rules.Items.All(row => row.ItemId == 3323), "真实青龙大会读取独立经验及曲玉奖励池");
        That(dungeons.Get(120).TournamentRewards!.Items.All(row => row.ItemId == 3329),
            "真实黄龙大会使用自身曲玉，不借用青龙或普通装备池");
        foreach (int boundary in new[] { 0, 59, 60, 94, 95, 99 })
        {
            int calls = 0;
            var plan = policy.PrepareTournament(blue, 2, bound => ++calls == 5 ? boundary : 0);
            That(plan.Normal.All(row => row.Gold > 0 && row.Item is null) &&
                plan.Champion.All(row => row.Item is { ItemId: 3323, Count: >= 1 and <= 3 }) &&
                plan.Champion[0].Item!.Count == (boundary < 60 ? 1 : boundary < 95 ? 2 : 3),
                "大会普通牌与冠军牌按各自PVF池生成，曲玉数量权重边界保持");
        }
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 121));
        int[] done = [3145, 4873, .. area.Quests, .. area.AnyQuests,
            .. towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(row => row.DungeonId == 121 && row.QuestId > 0)
                .Select(row => row.QuestId)];
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        for (int scenario = 0; scenario < 3; scenario++)
        {
            bool full = scenario == 2;
            using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 80, done,
                position: position, items: [(4727,2)], loseTournament: scenario == 1, inventorySetup: full ? Fill : null);
            var entered = f.Session.EnterFormalDungeon(121, 0, 2, dungeons, rooms);
            That(entered is { Entry.IsTournament: true }, "大会正式入场：" + f.Session.LastFormalDungeonRejection);
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
            {
                var death = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null, "合成大会轮次死亡提交");
                receipt = death.Receipt!;
            }
            var settled = f.Session.SettleFormalDungeon(f.Read().Id, 99, 99, [1, 2, 3], dungeons, clear, growth, policy);
            That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Reward is { BaseExperience: 500000, RankExperience: 0 } &&
                settled.Card is null && settled.Options is null, "大会不创建普通经验评分牌、黄金牌或黑钻卡牌账本");
            var frozen = f.Session.TournamentRewardState()!;
            string snapshot = JsonSerializer.Serialize(frozen.Reward);
            That(f.Session.SelectTournamentCard(0, 0) is null, "大会状态握手前不接受选牌");
            var repeated = f.Session.SettleFormalDungeon(f.Read().Id, 99, 99, [1, 2, 3], dungeons, clear, growth, policy);
            That(repeated.Outcome == DungeonRunOutcome.Duplicate &&
                JsonSerializer.Serialize(f.Session.TournamentRewardState()!.Reward) == snapshot,
                "重复通关报告保留四张原牌，不重抽或重置选择");
            var opened = f.Session.OpenTournamentCards()!;
            That(f.Session.ScheduleTournamentCard(opened.Receipt) && !f.Session.ScheduleTournamentCard(opened.Receipt),
                "大会重复状态请求不重置原自动选牌计时资格");
            var before = f.Read();
            var first = f.Session.SelectTournamentCard(0, 0);
            That(first is { Complete: false } && Same(before, f.Read()) &&
                f.Query("SELECT count(*) FROM character_dungeon_tournament_rewards") is 0L,
                "普通牌选择先展示归属，冠军牌未选时不提前发放资产");
            That(f.Session.SelectTournamentCard(0, 1) is null && f.Session.SelectTournamentCard(2, 0) is null &&
                f.Session.SelectTournamentCard(1, 2) is null, "改选与越界卡位不能改变已冻结选择");
            if (scenario == 0)
            {
                f.Sql("CREATE TRIGGER tournament_fail BEFORE INSERT ON character_dungeon_tournament_rewards BEGIN SELECT RAISE(ABORT,'tournament late rollback'); END");
                Reject<SqliteException>(() => f.Session.SelectTournamentCard(1, 0), "大会领取回执晚期故障回滚全部奖励");
                That(Same(before, f.Read()) && Same(before, f.Session.Selected!),
                    "大会晚期回滚保留数据库及会话原金币和物品");
                f.Sql("DROP TRIGGER tournament_fail");
            }
            else if (scenario == 1)
            {
                Reject<IOException>(() => f.Session.SelectTournamentCard(1, 0), "丢弃真实SQL提交后的首次返回值");
                That(f.Query("SELECT count(*) FROM character_dungeon_tournament_rewards") is 1L,
                    "结果丢失时已有唯一资产回执，恢复不能再次发奖");
            }
            var selected = (full ? f.Session.RevealTournamentCards(opened.Receipt) : f.Session.SelectTournamentCard(1, 0))!;
            var after = f.Read();
            var requested = frozen.Reward.Selected(selected.Choices);
            That(selected is { Complete: true, Inventory.Receipt: not null } &&
                after.Inventory.Gold == before.Inventory.Gold + requested.Sum(row => row.Gold) &&
                f.Session.CanPublishTournamentCards(selected) && f.Session.CanPublishInventory(selected.Inventory!.Receipt!) &&
                f.Query("SELECT count(*) FROM character_dungeon_tournament_rewards") is 1L,
                "两组选完后同事务发放金币和曲玉，投影绑定实际提交角色");
            That(full ? selected.MailId is not null && f.Query("SELECT count(*) FROM mailbox_attachments") is 1L :
                selected.MailId is null && after.Inventory.Items.Where(row => row.Core.ItemId == 3323).Sum(row => row.Core.Count) == requested[1].Item!.Count,
                $"大会材料完整入包，分类满时进入永久邮件：scenario={scenario},kind={requested[1].Item!.Kind},mail={selected.MailId}");
            That(f.Session.SelectTournamentCard(1, selected.Choices[1]) is { Complete: true, Duplicate: true, Inventory: null } && Same(after, f.Read()) &&
                f.Session.OpenTournamentCards()!.Choices.SequenceEqual(selected.Choices),
                "重复选择和状态请求只回放，不重新发奖或清除卡位");
            That(!f.Session.CanPublishTournamentCards(opened), "领取后旧角色或旧选择快照不能覆盖新牌面");
        }

        InventoryPlan Fill(Character role)
        {
            var template = items.Items.Get(3329);
            That(template.Kind == items.Items.Get(3323).Kind, "满包夹具使用同分类真实曲玉模板");
            var range = ItemSlotBoundService.Range(template.Kind, role.Inventory);
            var rows = role.Inventory.Items;
            foreach (short slot in Enumerable.Range(range.Start, range.End - range.Start + 1)
                .Concat(Enumerable.Range(ItemSlotBoundService.QuickSlotStart, 6)).Select(value => checked((short)value)).Distinct())
            {
                var position = new InventorySlot(ItemSlotBoundService.List(template.Kind),slot);
                if (rows.Any(row => row.Position == position) || !ItemSlotBoundService.Allows(role.Inventory, position, template.Kind)) continue;
                rows = rows.Add(new(0, position, InventoryRewardGrantService.Create(template)));
            }
            return new(role.Inventory, role.Inventory with { Items = rows }, "tournament_full_fixture");
        }
    }

    static bool Same(Character left, Character right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
