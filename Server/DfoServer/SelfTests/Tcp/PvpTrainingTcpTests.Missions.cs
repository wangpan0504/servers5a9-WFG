using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PvpTrainingTcpTests
{
    static void MissionAtomic(TcpTestFixture f, TcpTestResources resources)
    {
        var repo = new CharacterRepository(f.Database, resources.Quests);
        var rating = new PvpRatingSettlement(repo, new(new PvpRatingCatalog(resources.Archive)));
        var book = new PvpMissionBook(new(resources.Archive), repo, rating, resources.ItemResources.Items,
            resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
        foreach (bool overflow in new[] { false, true })
        {
            var owners = new AccountOwners();
            using var owner = owners.Acquire("mission-atomic-" + (overflow ? "full" : "bag"), () => { });
            var seed = CharacterCreation.Prepare("Mission" + overflow, resources.Characters.Get(0),
                new(10, 0, 474, 234), resources.Towns.InitialReturn, resources.Learning.Get);
            if (overflow)
            {
                var template = resources.ItemResources.Items.Get(10092640);
                var (start, end) = ItemSlotBoundService.Range(template.Kind, seed.Inventory);
                var filled = Enumerable.Range(start, end - start + 1).Select(slot => new InventoryItem(0,
                    new(InventoryListType.Main, (short)slot), InventoryRewardGrantService.Create(template, template.StackLimit)));
                seed = seed with { Inventory = seed.Inventory with { Items = seed.Inventory.Items.AddRange(filled) } };
            }
            var character = repo.Create(owner, seed);
            var fact = new PvpMissionFact(0, character.Job, 0, Guid.NewGuid());
            var plan = book.Plan(owner, character, fact);
            That(plan.Missions.SequenceEqual(new ushort[] { 565 }) && plan.Inventory.Plan!.OverflowItems.IsEmpty != overflow,
                "真实资源规划一次连招奖励，满材料栏只走溢出邮件");
            string before = Snapshot();
            f.Sql($"CREATE TRIGGER mission_fail BEFORE INSERT ON character_pvp_mission_rewards WHEN new.character_id={character.Id} BEGIN SELECT RAISE(ABORT,'mission fail'); END");
            Reject<SqliteException>(() => repo.CommitPvpMissions(owner, plan), "奖励回执失败使先前资产、UID、邮件和训练事实全部回滚");
            That(before == Snapshot(), "mission_atomic_rollback: 事务失败没有遗留物品、事实、奖励回执或邮件");
            f.Sql("DROP TRIGGER mission_fail");
            character = repo.CommitPvpMissions(owner, plan);
            That(character.Revision == plan.Expected.Revision + 1 && repo.ReadPvpMissionState(owner, character.Id).Claimed.SetEquals([565]),
                "资产与训练事实及一次性奖励共同提交，角色只增一次revision");
            That(character.Inventory.WinPoints == plan.Expected.Inventory.WinPoints, "胜利证明不误发成胜点");
            if (overflow)
                That(f.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0", character.Id) is [[1L]] &&
                    f.Query("SELECT count(*) FROM mailbox_attachments a JOIN mailbox_messages m USING(mail_id) JOIN character_inventory_items i ON i.item_uid=a.item_uid WHERE m.recipient_id=$p0 AND json_extract(i.item_core,'$.ItemId')=10092640 AND json_extract(i.item_core,'$.Value')=30", character.Id) is [[1L]],
                    "背包满时完整30个证明以真实UID邮件入库");
            else That(character.Inventory.Items.Where(item => item.Core.ItemId == 10092640).Sum(item => item.Core.Count) == 30 &&
                character.Inventory.Items.All(item => item.Uid > 0), "成功只发30个真实证明，发布使用已分配UID");
            before = Snapshot();
            Reject<InvalidOperationException>(() => repo.CommitPvpMissions(owner, plan), "旧规划重复提交不能再次发奖");
            var replay = book.Plan(owner, character, fact);
            That(replay.Missions.IsEmpty && replay.Inventory.Plan is { Changed: false } && before == Snapshot(),
                "完成重放只读，不重新分配奖励或邮件");
            var wrong = book.Plan(owner, character, new(0, 3, 4, Guid.NewGuid()));
            Reject<InvalidDataException>(() => repo.CommitPvpMissions(owner, wrong), "不同职业事实整批拒绝");
            That(before == Snapshot(), "职业拒绝不改变资产或事实");
            var stale = book.Plan(owner, character with { Revision = character.Revision - 1 }, new(0, 0, 4, Guid.NewGuid()));
            Reject<InvalidOperationException>(() => repo.CommitPvpMissions(owner, stale), "旧revision不能写训练或奖励");
            var reopened = new CharacterRepository(f.Database, resources.Quests);
            That(reopened.ReadPvpMissionState(owner, character.Id).Same(repo.ReadPvpMissionState(owner, character.Id)) && before == Snapshot(),
                "真实仓储重开保留训练及领取状态，不重复发奖");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repo.CommitPvpMissions(owner, replay), "已被接管的旧owner不能写事实或奖励");
            That(before == Snapshot(), "旧generation拒绝不改变任何资产");

            string Snapshot() => string.Join('|', new[] { "characters", "character_inventory_items", "character_pvp_mission_facts",
                "character_pvp_mission_rewards", "character_pvp_daily_missions", "mailbox_messages", "mailbox_attachments", "currency_audit_log", "sqlite_sequence" }
                .Select(table => f.Snapshot("SELECT * FROM " + table + " ORDER BY rowid")));
        }
    }

    static void MissionPractice(TcpTestFixture f, TcpTestResources resources, ushort id, string account, int port)
    {
        byte[] create = [0, 2, 0, 0, 0, 0xD1, 0xB5, 0, 0, 0, 1, 0];
        using (var peer = Open(f, account, gamePort: port, channelNumber: 101))
        {
            peer.Select(prepared: true); peer.Barrier();
            peer.Travel(10, 1, 450, 234, 10, 0); peer.Barrier();
            string before = f.Snapshot();
            Combo(0); peer.Send(881, [0]); peer.Barrier();
            That(before == f.Snapshot() && f.Query("SELECT count(*) FROM character_pvp_mission_facts WHERE character_id=$p0", id) is [[0L]],
                "大厅裸411及881不产生训练事实或奖励");
            Enter(); Combo(12); peer.Barrier();
            That(before == f.Snapshot(), "越界连招报告不写库、不发假成功");
            foreach (int index in new[] { 0, 4, 8 })
            {
                Combo(index); peer.Expect(0,14);
                var rows = PvpMissionTcpChecks.Update(peer, f);
                That(rows[(ushort)(565 + index / 4 * 13)].Current == 1, "训练后立即更新对应档任务，无需重登");
                peer.Barrier(); before = f.Snapshot();
                Combo(index); peer.Barrier();
                That(before == f.Snapshot(), "同实例重复报告不重发证明或更新revision");
            }
            That(Emblems() == 190 && f.Query("SELECT count(*) FROM character_pvp_mission_rewards WHERE character_id=$p0", id) is [[3L]],
                "三档连招准确发30加60加100，各只有一份回执");
            peer.Send(52,[0,254]); peer.Expect(0,43,[(byte)id,(byte)(id >> 8),6,1,0,0,255,255]);PvpArenaTcpTests.TownReturn(peer);
            Combo(1); peer.Barrier();
            That(f.Query("SELECT count(*) FROM character_pvp_mission_facts WHERE character_id=$p0", id) is [[3L]],
                "退出后的迟到报告不能复用原实例");

            var catalog = new PvpArcadeCatalog(resources.Archive);
            foreach (int difficulty in new[] { 0, 2 })
            {
                var plan = catalog.Modes.Values.Where(mode => mode.Difficulty == difficulty)
                    .Select(mode => catalog.Resolve(0, 0, mode.Index)).Where(row => row is not null)
                    .OrderBy(row => row!.Allowed.Length).ThenBy(row => row!.Mode.Index).First()!;
                byte[] arcade = (byte[])create.Clone(); arcade[^2] = 3; arcade[^1] = checked((byte)(plan.Mode.Index + 1));
                peer.Send(50, arcade); peer.Expect(1,50,[1]);
                That(peer.Expect(0,41).Body[13] == 10, "无风云任务也能创建资源绑定的道场实例");
                before = f.Snapshot();
                Report(1, plan.Allowed[0].First(), 1, true); peer.Send(881,[(byte)plan.Mode.Index]); peer.Barrier();
                That(before == f.Snapshot(), "缺少加载序列的胜利和881信号不能领奖");
                var used = new HashSet<int>();
                for (int round = 1; round <= plan.Allowed.Length; round++)
                {
                    int opponent = plan.Allowed[round - 1].First(value => !plan.RandomRounds.Contains(round) || !used.Contains(value));
                    if (plan.RandomRounds.Contains(round)) used.Add(opponent);
                    Report(0, opponent, round, false);
                    if (round == plan.Allowed.Length)
                    {
                        peer.Send(881,[(byte)plan.Mode.Index]); peer.Barrier();
                        That(before == f.Snapshot(), "末轮获胜提示先于完整1287报告时仍不发奖");
                    }
                    Report(1, opponent, round, true);
                }
                peer.Expect(0,14);
                var rows = PvpMissionTcpChecks.Update(peer, f);
                That(rows[604].Current == 1 && rows[605].Current == (difficulty == 2 ? 1 : 0), "完整道场序列即时推进实际难度");
                peer.Barrier(); before = f.Snapshot();
                Report(1, plan.Allowed[^1].First(), plan.Allowed.Length, true); peer.Barrier();
                That(before == f.Snapshot(), "末轮重放不重复袖珍罐");
                peer.Send(13); peer.Expect(0,43,[(byte)id,(byte)(id >> 8),10,1,0,0,255,255]);PvpArenaTcpTests.TownReturn(peer); peer.Barrier();

                void Report(byte action, int opponent, int round, bool won) => peer.Send(1287, Build(writer =>
                { writer.Write(action); writer.Write(opponent); writer.Write((byte)plan.Allowed.Length); writer.Write((byte)round);
                    writer.Write(0u); writer.Write((byte)0); writer.Write(won); writer.Write((ushort)0); writer.Write(0u); }));
            }
            That(f.Query("SELECT count(*) FROM character_pvp_mission_rewards WHERE character_id=$p0", id) is [[5L]] &&
                f.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id BETWEEN 2670 AND 2679", id) is [[0L]],
                "连招及道场五项MSN奖励独立闭合，不伪造风云接取记录");
            peer.Send(3,[1]); peer.Expect(1,3,[1]); peer.Closed();

            void Combo(int index) => peer.Send(411, Build(writer => writer.Write(index)));
            void Enter() { peer.Send(50,create); peer.Expect(1,50,[1]); peer.Expect(0,41); }
        }
        using (var peer = Open(f, account, gamePort: port, channelNumber: 101))
        {
            peer.Select(prepared:true); peer.Barrier();
            var rows = PvpMissionTcpChecks.Parse(peer.PvpMissions);
            That(rows[565].Current == 1 && rows[578].Current == 1 && rows[591].Current == 1 && rows[605].Current == 1 && Emblems() == 190,
                "重登回放全部训练进度且不重复发奖");
            peer.Send(411, Build(writer => writer.Write(0))); peer.Send(881,[0]); peer.Barrier();
            That(Emblems() == 190, "重登后的旧裸报告不能恢复训练实例或重复发奖");
        }
        long Emblems() => (long)f.Query("SELECT COALESCE(SUM(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10092640",id)[0][0];
    }
}
