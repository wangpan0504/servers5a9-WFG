using System.Buffers.Binary;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    // 调用方已通过真实44使用潜能药；继续复用原入场、加载、收包及战况观察，不直接改在线角色。
    static List<WirePacket>[] VerifyKeyRecovery(TcpTestFixture fixture, TcpPeer[] team, List<WirePacket>[] entry,
        Func<TcpPeer, List<WirePacket>> drain, Func<List<WirePacket>[]> reenter)
    {
        That(team.Length is >= 2 and <= 4 && entry.Length == team.Length, "对象键恢复使用同遭遇真实多人成员");
        byte[][] headers = entry.Select(CurrentOffer).ToArray();
        byte[] originalRun = headers[0].AsSpan(24, 16).ToArray();
        That(headers.All(header => header.AsSpan(24, 16).SequenceEqual(originalRun)), "各成员原租约归同一正式Run");
        foreach (var peer in team)
            That(fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND item_id=10312385 AND (maintenance & 2)=0", peer.Id)
                is [[long count]] && count > 0, "真实44的潜能药具有Town清理状态，不能用无需写库的退场冒充事务回滚");

        string before = FullState();
        string economy = Economy();
        var revisions = team.Select(peer => Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0])).ToArray();
        var attempts = team.Select(peer => Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)[0][0])).ToArray();
        byte[] cancel = Cancel(headers[0], 6);
        foreach (int identityOffset in new[] { 8, 40, 64 })
        {
            byte[] invalid = (byte[])cancel.Clone();
            invalid[identityOffset] ^= 0x80;
            team[0].Send(FiendwarKeyLeaseRequests.Opcode, invalid);
            var rejected = DrainAll(0);
            That(rejected.All(rows => !rows.Any(IsCancel)), "错误batch、lease或MAI身份不能暂停原Run或发布恢复通知");
            NoOutcome(rejected, returning: false);
            That(FullState() == before, "错误恢复身份不写角色、物品、称号或攻坚持久账本");
        }

        const string marker = "fiendwar-key-recovery-late-rollback";
        const string trigger = "fiendwar_key_recovery_late_failure";
        int eventBoundary = fixture.Events().Length;
        // 不仅要求最后一人报错：在同一事务内核实此前每人已推进revision并清理药效，错误文本证明走过真实写入。
        string priorWrites = string.Join(" AND ", team.Take(team.Length - 1).Select((peer, index) =>
            $"(SELECT revision FROM characters WHERE id={peer.Id})>{revisions[index]} AND " +
            $"NOT EXISTS(SELECT 1 FROM character_consumable_states WHERE character_id={peer.Id} AND (maintenance & 2)=0)"));
        fixture.Sql($"""
            CREATE TRIGGER {trigger} BEFORE UPDATE OF revision ON characters
            WHEN NEW.id={team[^1].Id} AND NEW.revision>OLD.revision
            BEGIN
              SELECT CASE WHEN {priorWrites}
                THEN RAISE(ABORT,'{marker}')
                ELSE RAISE(ABORT,'fiendwar-key-recovery-order-not-proven') END;
            END
            """);
        List<WirePacket>[] paused;
        try
        {
            team[0].Send(FiendwarKeyLeaseRequests.Opcode, cancel);
            // 1278仍须应答；SQL失败关闭首报告连接时本断言应真实失败，不能改成忽略该连接。
            paused = DrainAll(0);
            // 1278证明命令已结束，不保证异步日志缓冲已刷盘；按事件等候，不用固定睡眠代替证据。
            That(SpinWait.SpinUntil(() => fixture.Events().Skip(eventBoundary).Any(row =>
                row.GetProperty("Kind").GetString() == "fiendwar_key_recovery_failed" &&
                row.GetProperty("Detail").GetProperty("Message").GetString()?.Contains(marker, StringComparison.Ordinal) == true), TimeSpan.FromSeconds(2)),
                "日志证明最后成员SQL触发器在此前成员实际写入后失败，不能用未执行的trigger充作回滚证据");
            for (int index = 0; index < team.Length; index++)
            {
                var notices = paused[index].Where(IsCancel).ToArray();
                That(notices.Length > 0 && notices.All(row => row.Body[5] == 2 &&
                    row.Body.AsSpan(8).SequenceEqual(headers[index].AsSpan(8))),
                    "SQL失败后每名成员收到自己的精确batch、lease和场景恢复通知");
            }
            NoOutcome(paused, returning: false);
            That(FullState() == before, "最后成员失败使此前成员revision、药效清理及所有经济和攻坚写入整体回滚");
        }
        finally { fixture.Sql($"DROP TRIGGER IF EXISTS {trigger}"); }

        int survivor = team.Length - 1;
        byte[] retry = (byte[])paused[survivor].Last(IsCancel).Body.Clone();
        team[survivor].Send(FiendwarKeyLeaseRequests.Opcode, retry);
        var returned = DrainAll(survivor);
        NoOutcome(returned, returning: true);
        for (int index = 0; index < team.Length; index++)
        {
            That(returned[index].Any(row => row is { Kind: 0, Opcode: 24, Body: [57, ..] }),
                "另一存活成员按自己的恢复身份重试，使全员真实返回漩涡候战城镇");
            That(fixture.Query("SELECT town FROM characters WHERE id=$p0", team[index].Id) is [[57L]] &&
                fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND (maintenance & 2)=0", team[index].Id) is [[0L]] &&
                Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", team[index].Id)[0][0]) > revisions[index],
                "恢复成功才提交各成员Town边界药效清理与新revision");
        }
        That(Economy() == economy, "故障退场保留已扣门票、原团进度与资产，不计通关、额外惩罚或发奖");

        var fresh = reenter();
        That(fresh.Length == team.Length, "恢复后仍由原完整入场框架重进全员");
        for (int index = 0; index < team.Length; index++)
        {
            byte[] header = CurrentOffer(fresh[index]);
            That(!header.AsSpan(24, 16).SequenceEqual(originalRun) &&
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40)) != 0 &&
                fresh[index].Count(row => row is { Kind: 0, Opcode: 28 }) == 1 &&
                fresh[index].Count(row => row is { Kind: 0, Opcode: 29 }) == 1,
                "恢复后新正式Run重新授予有效租约且全员只完整加载一次");
            That(Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", team[index].Id)[0][0]) == attempts[index] + 1,
                "故障恢复不增加尝试号，新Run重进才通过原入场事务增加一次");
        }
        That(Economy() == economy, "同团重新挑战不重扣攻坚门票，不增加奖励、通关或撤退惩罚");
        string freshState = FullState();
        team[0].Send(FiendwarKeyLeaseRequests.Opcode, cancel);
        var stale = DrainAll(0);
        That(stale.All(rows => !rows.Any(IsCancel)) && FullState() == freshState,
            "旧Run准确取消包也不能暂停、回退或释放新Run");
        NoOutcome(stale, returning: false);
        return fresh;

        List<WirePacket>[] DrainAll(int first)
        {
            var rows = new List<WirePacket>[team.Length];
            foreach (int index in Enumerable.Range(0, team.Length).OrderBy(index => index == first ? 0 : 1))
                rows[index] = drain(team[index]);
            return rows;
        }

        string FullState() => string.Join('|', team.Select(peer => string.Join('|', new[]
        {
            fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", peer.Id),
            fixture.Snapshot("SELECT * FROM character_consumable_states WHERE character_id=$p0 ORDER BY kind,item_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id),
            fixture.Snapshot("SELECT * FROM inventory_audit_log WHERE character_id=$p0 ORDER BY audit_id", peer.Id),
            EconomyFor(peer)
        })));

        string Economy() => string.Join('|', team.Select(EconomyFor));
        string EconomyFor(TcpPeer peer) => string.Join('|', new[]
        {
            fixture.Snapshot("SELECT experience,gold,revive_coins,stamina_until FROM characters WHERE id=$p0", peer.Id),
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id),
            fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", peer.Id),
            fixture.Snapshot("SELECT * FROM character_raid_runs WHERE character_id=$p0 ORDER BY instance_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_raid_reward_receipts WHERE character_id=$p0 ORDER BY instance_id,phase,kind", peer.Id),
            fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0 ORDER BY event_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_fiendwar_title_facts WHERE character_id=$p0 ORDER BY quest_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_fiendwar_counter_stream WHERE character_id=$p0", peer.Id)
        });
    }

    static byte[] CurrentOffer(List<WirePacket> rows)
    {
        var packet = rows.LastOrDefault(row => row is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode, Body.Length: 80 } &&
            row.Body[4] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(row.Body.AsSpan(64)) != 0);
        That(packet is not null, "恢复测试必须取得真实MAI之后的本成员FWL场景身份");
        return (byte[])packet!.Body.Clone();
    }

    static byte[] Cancel(byte[] header, byte status)
    {
        byte[] request = (byte[])header.Clone();
        request[4] = 5; request[5] = status; request[6] = request[7] = 0;
        return request;
    }

    static bool IsCancel(WirePacket packet) => packet is { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode, Body.Length: 80 } && packet.Body[4] == 5;

    static void NoOutcome(List<WirePacket>[] rows, bool returning)
    {
        That(rows.All(frames => !frames.Any(row => row is { Kind: 0, Opcode: 31 or 34 or 35 or 37 or 38 or 596 or 597 or 600 or 601 } ||
            row is { Kind: 0, Opcode: 1523, Body: [3, 0, 0] })), "对象键恢复不伪造击杀、通关、翻牌或团本胜利");
        That(rows.All(frames => !frames.Any(row => row is { Kind: 0, Opcode: 27 or 28 or 29 } ||
            !returning && row is { Kind: 0, Opcode: 23 or 24 })), "SQL失败及过期取消不提前切场，新Run入场只由原框架执行");
    }
}
