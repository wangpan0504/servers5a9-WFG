using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class HellPartyEntryTests
{
    static void LegendPassCore()
    {
        System.Collections.Immutable.ImmutableArray<LegendHellPassCost> Parse(params int[] values) => LegendHellPassCost.Parse(
            [new(3, 1), .. values.Select(v => new PvfToken(0, v)), new(3, 2)],
            id => id == 1 ? "[hell legend freepass item]" : "[/hell legend freepass item]");
        That(Parse(10307036, 1, 490703917, 1).SequenceEqual([
                new LegendHellPassCost(10307036, 1), new(490703917, 1), new(CosmoPassCost.DailyAttendanceStarPass, 1)]) &&
            Parse(CosmoPassCost.DailyAttendanceStarPass, 1).SequenceEqual([new LegendHellPassCost(CosmoPassCost.DailyAttendanceStarPass, 1)]),
            "星空裂缝通行证二元组保留券数量，补充日签到星空裂缝通行证且不重复添加");
        foreach (int[] invalid in new int[][] { [10307036], [10307036, 0], [0, 1], [10307036, 1, 10307036, 1] })
            Reject<InvalidDataException>(() => Parse(invalid), "星空裂缝通行证拒绝残缺、零数量与重复编号");
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        var pieces = new EpicPieceCatalog("[piece list]\n700001 700002\n[/piece list]");
        LegendHellPassCost[] candidates = [new(10307036, 1), new(490703917, 1), new(CosmoPassCost.DailyAttendanceStarPass, 1)];
        InventoryItem expired = new(1, new(InventoryListType.Main, 121), new(10307036, ItemKind.Material, 1) { ExpireTime = 2_000_000_000 });
        InventoryItem locked = new(2, new(InventoryListType.Main, 122), new(490703917, ItemKind.Material, 1) { EquipmentLockId = 1 });
        InventoryItem cargo = new(3, new(InventoryListType.PersonalCargo, 0), new(CosmoPassCost.DailyAttendanceStarPass, ItemKind.Material, 1));
        InventorySnapshot unavailable = new([expired, locked, cargo]);
        That(HellPartyEntryCost.LegendPass(unavailable, candidates, pieces, now).Plan is null,
            "到期、锁定及仓库中的星空裂缝通行证不能抵扣星空裂缝入场");
        var valid = unavailable with { Items = unavailable.Items
            .Add(new(4, new(InventoryListType.Main, 124), new(10307036, ItemKind.Material, 1)))
            .Add(new(5, new(InventoryListType.Main, 123), new(CosmoPassCost.DailyAttendanceStarPass, ItemKind.Material, 1))) };
        That(HellPartyEntryCost.LegendPass(valid, candidates, pieces, now).Ticket == new HellPartyTicket(CosmoPassCost.DailyAttendanceStarPass, 1),
            "星空裂缝通行证按有效背包槽位顺序选择，一次只消耗一张");
    }

    internal static void RunLegendPass(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, HashSet<int> completed, Location position)
    {
        var passes = items.Items.LegendHellPasses();
        That(passes.Length == 19 && passes.All(row => row.Count == 1 && items.Items.Get(row.ItemId).Kind == ItemKind.Material) &&
            passes.Any(row => row.ItemId == 10307036) && passes.Any(row => row.ItemId == 490703917) &&
            passes.Any(row => row.ItemId == CosmoPassCost.DailyAttendanceStarPass) &&
            !passes.Any(row => row.ItemId is OrdinaryHellPassCost.LegacyInvitationPass or 490705255),
            "星空裂缝通行证匹配freepassitem的18种与签到星空通行证490013043共19种，不混入普通深渊券或超星空专属券");
        var legendDone = completed.Append(6081).Distinct().Order().ToArray();
        var drops = Testing.SelfTestCatalogs.MonsterDrops(pvf);
        using (var ordinaryReject = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
            legendDone, position: position, items: [(490703917, 1), (CosmoPassCost.DailyAttendanceStarPass, 1)]))
        {
            var before = ordinaryReject.Read();
            That(ordinaryReject.Session.EnterFormalDungeon(291100308, 0, 4, dungeons, rooms,
                drops: drops, items: items.Items, pieces: items.EpicPieces, hellParty: true) is null &&
                ordinaryReject.Session.LastFormalDungeonRejection == "hell-party-item-required" &&
                QuestCompletionRepositoryTests.Same(before, ordinaryReject.Read()),
                "星空裂缝通行证不能充当普通深渊通行证免费进入普通深渊");
        }
        foreach (int passId in new[] { 10307036, 490703917, CosmoPassCost.DailyAttendanceStarPass })
        {
            using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
                legendDone, position: position, items: [(passId, 2), (OrdinaryHellPassCost.LegacyInvitationPass, 1), (3330, 30), (HellPartyEntryCost.LegendStone, 2)]);
            f.Session.BeginDungeonSelection(); f.Session.PrepareLegendDungeonSelection(dungeons, _ => 2);
            var saved = f.Read();
            f.Sql("CREATE TRIGGER fail_legend_pass AFTER UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'legend pass rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => Enter(), "星空裂缝通行证入场提交失败须回滚");
            That(QuestCompletionRepositoryTests.Same(saved, f.Read()) && f.Session.DungeonSnapshot is null,
                "星空裂缝通行证、普通券、邀请函、灵石与尝试号同事务回滚");
            f.Sql("DROP TRIGGER fail_legend_pass");
            var receipt = Enter();
            var after = f.Read();
            That(receipt is { Entry.IsHellLegend: true, HellTicket.Count: 1, LegendTicket: null } &&
                receipt.HellTicket.ItemId == passId && receipt.HellConsumedSlots.Length == 1 &&
                after.Inventory.Items.Where(row => row.Core.ItemId == passId).Sum(row => row.Core.Count) == 1 &&
                after.Inventory.Items.Where(row => row.Core.ItemId == OrdinaryHellPassCost.LegacyInvitationPass).Sum(row => row.Core.Count) == 1 &&
                after.Inventory.Items.Where(row => row.Core.ItemId == 3330).Sum(row => row.Core.Count) == 30 &&
                after.Inventory.Items.Where(row => row.Core.ItemId == HellPartyEntryCost.LegendStone).Sum(row => row.Core.Count) == 2,
                "星空裂缝优先消耗1张星空裂缝通行证并免除邀请函与灵石：" + passId);
            FormalDungeonReceipt? Enter() => f.Session.EnterFormalDungeon(291100308, 0, 4, dungeons, rooms,
                drops: drops, items: items.Items, pieces: items.EpicPieces, hellParty: true, hellLegend: true);
        }
    }

    internal static void RunSealRetry(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, HashSet<int> completed, Location position)
    {
        var kills = Testing.SelfTestCatalogs.KillExperience(pvf);
        var clear = Testing.SelfTestCatalogs.ClearExperience(pvf);
        var drops = Testing.SelfTestCatalogs.MonsterDrops(pvf);
        foreach (bool legend in new[] { false, true })
        {
            using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
                completed.Append(6081).Distinct().ToArray(), position: position,
                items: [(3330, legend ? 90 : 60), (HellPartyEntryCost.LegendStone, 4)]);
            That(f.Session.BeginDungeonSelection() is not null, "深渊重试先正常选图");
            if (legend) That(f.Session.PrepareLegendDungeonSelection(dungeons, _ => 2) == 291100308,
                "传奇深渊按真实选图冻结目标");
            var first = f.Session.EnterFormalDungeon(291100308, 0, 4, dungeons, rooms,
                pieces: items.EpicPieces, hellParty: true, hellLegend: legend)!;
            That(first?.Entry.HellParty is not null, "普通/传奇深渊首次入场");
            FormalDungeonReceipt Finish(FormalDungeonReceipt opening)
            {
                var path = RiftPath(opening.Entry.Maze, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                for (int step = 0; ; step++)
                {
                    foreach (var monster in loaded.Monsters.Monsters.Where(row => row.IsBlocking))
                    {
                        var death = f.Session.ObserveFormalDungeonDeath(new(monster.ObjectKey, (ushort)f.Read().Id, 0x0111,
                            (ushort)f.Read().Id, 600, 3, 0, (uint)monster.CandidateLevel, 504, 620, 3, true, new string('E', 64)),
                            kills, growth);
                        That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null, "深渊逐房真实死亡提交");
                        loaded = death.Receipt!;
                    }
                    if (loaded.Snapshot.CurrentMap.Position == opening.Entry.BossMap.Position) break;
                    That(step < path.Count && f.Session.MoveFormalDungeon((byte)path[step].X, (byte)path[step].Y) is not null,
                        "深渊沿真实路线走到Boss房");
                    loaded = f.Session.CompleteFormalDungeonLoading()!;
                }
                var result = f.Session.SettleFormalDungeon((ushort)f.Read().Id, 99, 99, [1, 2, 3], dungeons, clear, growth);
                That(result.Outcome == DungeonRunOutcome.Accepted && result.Receipt is not null, "深渊Boss真实结算");
                return result.Receipt!;
            }
            var settled = Finish(first!);
            var before = f.Read();
            f.Sql("CREATE TRIGGER fail_hell_retry BEFORE UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'hell retry rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Session.RetryFormalDungeon(settled, dungeons,
                rooms, drops, items.Items, items.EpicPieces), "深渊重试入场提交失败须抛出");
            That(QuestCompletionRepositoryTests.Same(before, f.Read()) && f.Session.FormalDungeonResultFocus() is not null,
                "失败回滚邀请函、灵石、疲劳、尝试号并保留原结算");
            f.Sql("DROP TRIGGER fail_hell_retry");
            var retry = f.Session.RetryFormalDungeon(settled, dungeons, rooms, drops, items.Items, items.EpicPieces);
            That(retry is not null && retry.Entry.HellParty is not null && retry.Entry.IsHellLegend == legend &&
                retry.Entry.HellParty.MapId == (legend ? 313169 : 313163) && retry.Entry.HellPartyMode == 1 &&
                retry.Entry.DungeonId == first!.Entry.DungeonId && retry.Entry.Difficulty == first.Entry.Difficulty &&
                retry.Snapshot.Identity.InstanceId != first.Snapshot.Identity.InstanceId &&
                retry.Snapshot.Identity.TaskAttempt == settled.Snapshot.Identity.TaskAttempt + 1 &&
                retry.HellTicket == new HellPartyTicket(3330, 30) &&
                retry.LegendTicket == (legend ? new HellPartyTicket(HellPartyEntryCost.LegendStone, 2) : null),
                "Boss结算再次挑战保留普通/传奇深渊并重新扣票：" + f.Session.LastFormalDungeonRejection);
            That(f.Read().Inventory.Items.Where(row => row.Core.ItemId == 3330).Sum(row => row.Core.Count) == (legend ? 30 : 0) &&
                f.Read().Inventory.Items.Where(row => row.Core.ItemId == HellPartyEntryCost.LegendStone).Sum(row => row.Core.Count) == (legend ? 0 : 4) &&
                f.Session.RetryFormalDungeon(settled, dungeons, rooms, drops, items.Items, items.EpicPieces) is null,
                "同一结算重放不能重复扣费或重建实例");
            var information = DungeonPackets.Information(retry!.Entry);
            That(BinaryPrimitives.ReadUInt16LittleEndian(information.AsSpan(14)) == 1 &&
                DungeonPackets.StartMap(retry.Entry, 7u)[7] == 1, "重试28/29通知保留深渊模式");
            var secondSettlement = Finish(retry);
            var noMaterials = f.Read();
            That(f.Session.RetryFormalDungeon(secondSettlement, dungeons, rooms, drops, items.Items, items.EpicPieces) is null &&
                f.Session.LastFormalDungeonRejection == "hell-party-item-required" &&
                QuestCompletionRepositoryTests.Same(noMaterials, f.Read()) && f.Session.FormalDungeonResultFocus() is not null,
                "邀请函或传奇灵石不足时不降为普通图、不扣费、不替换原结算");
        }
    }
}
