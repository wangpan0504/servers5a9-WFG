using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class HellPartyEntryTests
{
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
