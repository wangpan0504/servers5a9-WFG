using System.Buffers.Binary;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    // 已加载、未击杀父怪时调用一次；报告人必须是当前临时副本队长，rooms为各成员的原MAI。
    internal static bool VerifyDynamicSpawns(TcpTestFixture fixture, TcpPeer[] team, byte[] map,
        Dictionary<ushort, byte[]> rooms)
    {
        That(team.Length is >= 1 and <= 4 && map.Length >= 19 + map[18] * 22, "动态召唤使用完整真实演员表和同遭遇成员");
        var parents = Enumerable.Range(0, map[18]).Select(index => 19 + index * 22)
            .Where(at => U32(map, at + 8) is 107013177 or 107013178 or 107013154 or 107013148 or
                109008128 or 109008129 or 109008113 or 109008107).ToArray();
        if (parents.Length == 0) return false;
        var leader = team[0];
        That(rooms.TryGetValue(leader.Id, out var room) && room.Length is 56 or 80,
            "动态生成通过正式MAI取得本房serial、地图和种子");
        bool guide = U32(room!, 8) is >= 5350 and <= 5358;
        var occupied = Enumerable.Range(0, map[18]).Select(index => U16(map, 19 + index * 22 + 6))
            .Concat(team.Select(peer => peer.Id)).ToHashSet();
        ushort[] staticCollisions = occupied.ToArray();
        uint sequence = 10000;
        string before = State();
        foreach (int at in parents)
        {
            uint parent = U32(map, at + 8);
            That(guide == (parent is 109008128 or 109008129 or 109008113 or 109008107),
                "引导与团队父模板来自各自原资源，子模板按实际ACT允许复用");
            uint[] children = parent switch
            {
                107013177 => [107013169, 107013170, 107013171, 107013172],
                107013178 => [107013188],
                107013154 => [107013155, 107013156],
                107013148 => [107013173, 107013174, 107013175, 107013176],
                109008128 => [109008120, 109008121, 109008122, 109008123],
                109008129 => [109008139],
                109008113 => [109008114, 109008115],
                109008107 => [107013173, 107013174, 107013175, 107013176],
                _ => throw new InvalidOperationException("未知漩涡动态父怪")
            };
            bool singleSlot = parent is 107013177 or 107013178 or 109008128 or 109008129;
            ushort parentKey = U16(map, at + 6);
            byte level = map[at + 12];
            ushort first = NextKey();
            byte[] valid = Report(leader, parentKey, children[0], first, sequence);
            byte[] zero = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(zero.AsSpan(36), 0);
            Send(leader, zero); NoDeath(0, level, "零键不能登记原生动态对象");
            byte[] wrongLease = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(wrongLease.AsSpan(40), ulong.MaxValue);
            Send(leader, wrongLease); NoDeath(first, level, "错误租约不能采用真实已授键");
            byte[] legacy = valid[..40];
            BinaryPrimitives.WriteUInt32LittleEndian(legacy, FiendwarSpawnRequests.Magic);
            Send(leader, legacy); NoDeath(first, level, "正式团队和引导不提供旧FWS1免租约旁路");
            foreach (int offset in new[] { 4, 8, 12, 16, 20, 28, 32, 36 })
            {
                byte[] bad = (byte[])valid.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(offset), uint.MaxValue);
                Send(leader, bad);
                NoDeath(first, level, "错误房间或身份没有安装动态对象");
            }
            // 有效key与错误模板均保留原seq；拒绝不能抢占本次正常报告的流或活跃槽。
            byte[] crossed = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(crossed.AsSpan(32), guide ? 107013169u : 109008120u);
            Send(leader, crossed); NoDeath(first, level, "未在当前父ACT声明的另一模式子模板不能使用");
            byte[] wrongFamily = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(wrongFamily.AsSpan(32), children[0] == 107013155 ? 107013173u : 107013155u);
            Send(leader, wrongFamily); NoDeath(first, level, "同属团队前缀也不能越过精确父子模板白名单");
            foreach (ushort collision in staticCollisions)
            {
                byte[] bad = (byte[])valid.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(36), collision);
                Send(leader, bad);
            }
            byte[] exhausted = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(exhausted.AsSpan(36), ushort.MaxValue);
            Send(leader, exhausted);
            NoDeath(ushort.MaxValue, level, "原生分配器耗尽哨兵FFFF不能登记为合法动态对象");
            if (team.Length > 1)
            {
                Send(team[1], Report(team[1], parentKey, children[0], first, sequence));
                NoDeath(first, level, "非当前临时队长不能安装动态对象");
            }
            var live = new List<(uint Monster, ushort Key, byte[] Report)>();
            Send(leader, valid); Send(leader, valid);
            live.Add((children[0], first, valid));
            ushort replacement = NextKey();
            Send(leader, Report(leader, first, children[0], replacement, sequence + 1));
            NoDeath(replacement, level, "新活跃子对象不能冒充正式静态父对象继续召唤");
            Send(leader, Report(leader, first, children[0], first, sequence + 1));
            // 错父拒绝后，原登记及原身份重放仍必须保留；不让错误报告改写活跃对象。
            Send(leader, valid);
            byte[] conflicting = Report(leader, parentKey, children.Length > 1 ? children[1] : children[0], replacement, sequence);
            Send(leader, conflicting); NoDeath(replacement, level, "同seq异正文不安装第二对象");
            if (children.Length > 1)
            {
                Send(leader, Report(leader, parentKey, children[1], replacement, sequence - 1));
                NoDeath(replacement, level, "同流低序号不能安装尚未占用的另一颜色");
            }
            Send(leader, Report(leader, parentKey, children[0], replacement, ++sequence));
            if (singleSlot)
                NoDeath(replacement, level, "同一父同色或全房屏障不能同时活跃两只");
            else
                // 黑白猫与暗幻影没有资源已证单模板上限，真实不同租约键可并发。
                live.Add((children[0], replacement, Report(leader, parentKey, children[0], replacement, sequence)));
            for (int color = 1; color < children.Length; color++)
            {
                ushort key = NextKey();
                byte[] report = Report(leader, parentKey, children[color], key, ++sequence);
                Send(leader, report); live.Add((children[color], key, report));
            }
            // 四色、两猫、四幻影及其合法同模板实例必须可并存，退休向同遭遇全员发原38。
            foreach (var actor in live)
            {
                Retire(actor.Key, level);
                Retire(actor.Key, level);
            }
            Send(leader, Report(leader, parentKey, children[0], first, ++sequence));
            // 原有限槽复用先前被拒键；猫/幻影采用新租约键进入下一轮，墓碑不复用。
            ushort nextWave = singleSlot ? replacement : NextKey();
            Send(leader, Report(leader, parentKey, children[0], nextWave, ++sequence));
            Retire(nextWave, level);
            Retire(first, level);
            Retire(nextWave, level);
            sequence++;
        }
        That(State() == before, "动态子怪不改变角色revision、经验、掉落、称号、任务和攻坚账本");
        if (team.Length > 1)
        {
            int at = parents[0];
            uint child = U32(map, at + 8) switch
            {
                107013177 => 107013169,
                107013178 => 107013188,
                107013154 => 107013155,
                107013148 => 107013173,
                109008128 => 109008120,
                109008129 => 109008139,
                109008113 => 109008114,
                109008107 => 107013173,
                _ => throw new InvalidOperationException("未知漩涡动态父怪")
            };
            // 真实死亡不移交房主；分别覆盖死队长仍报告AI、死队员仍参与全员安装两条路径。
            foreach (var dead in new[] { leader, team[^1] })
            {
                dead.Send(40, [0x74, 0, 4, 1, 0, 0]);
                var deathNotices = team.OrderBy(peer => peer == dead ? 0 : 1)
                    .Select(peer => AntonTcpProbe.Barrier(peer, "fiendwar dynamic dead member")).ToArray();
                That(deathNotices.All(rows => rows.Count(row => row is { Kind: 0, Opcode: 32, Body.Length: 4 } &&
                    U16(row.Body) == dead.Id && row.Body[2] == 0) == 1),
                    "真实40向全员提交死亡状态，再验证死队长或死队员下的原生召唤");
                string deadState = State();
                long coins = Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", dead.Id)[0][0]);
                ushort key = NextKey();
                Send(leader, Report(leader, U16(map, at + 6), child, key, ++sequence));
                Retire(key, map[at + 12]);
                Retire(key, map[at + 12]);
                That(State() == deadState,
                    "成员仍死亡时全员动态生成、退休及38重放不改变经济、经验、任务、称号或攻坚账本");
                dead.Send(41, BitConverter.GetBytes(dead.Id));
                var revivalNotices = team.OrderBy(peer => peer == dead ? 0 : 1)
                    .Select(peer => (Peer: peer, Rows: AntonTcpProbe.Barrier(peer, "fiendwar dynamic member revived"))).ToArray();
                That(revivalNotices.Single(row => row.Peer == dead).Rows.Count(row =>
                        row is { Kind: 1, Opcode: 41, Body: [1, _, _, 0] } && U16(row.Body, 1) == dead.Id) == 1 &&
                    revivalNotices.All(row => row.Rows.Count(packet => packet is { Kind: 0, Opcode: 32, Body.Length: 4 } &&
                        U16(packet.Body) == dead.Id && packet.Body[2] == 1) == 1) &&
                    Convert.ToInt64(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", dead.Id)[0][0]) == coins - 1,
                    "真实41扣一枚共享额度内的个人币并向全员恢复生命，不把测试死亡留给后续流程");
            }
        }
        return true;

        ushort NextKey()
        {
            ushort key = AntonTcpProbe.TakeFiendwarKey(leader);
            That(key is > 0 and < ushort.MaxValue && occupied.Add(key), "动态测试仅采用本成员真实grant/ready的未用键");
            return key;
        }

        byte[] Report(TcpPeer peer, ushort sourceKey, uint monster, ushort key, uint seq) => Build(writer =>
        {
            byte[] identity = rooms[peer.Id];
            writer.Write(FiendwarSpawnRequests.MagicV2); writer.Write(U32(identity, 4));
            writer.Write(U32(identity, 8)); writer.Write(U32(identity, 12)); writer.Write(U32(identity, 16));
            writer.Write((uint)(map[0] | map[1] << 8)); writer.Write(seq);
            writer.Write((uint)sourceKey); writer.Write(monster); writer.Write((uint)key);
            writer.Write(AntonTcpProbe.FiendwarLease(peer));
        });

        void Send(TcpPeer peer, byte[] body)
        {
            peer.Send(FiendwarSpawnRequests.Opcode, body);
            var rows = AntonTcpProbe.Barrier(peer, "fiendwar dynamic registration");
            That(!rows.Any(row => row.Opcode == FiendwarSpawnRequests.Opcode || row is { Kind: 0, Opcode: 38 }),
                "生成私包只登记身份，不伪造ACK或死亡");
        }

        List<WirePacket>[] Death(ushort key, byte level)
        {
            leader.Send(39, FormalDungeonTcpTests.Death(key, leader.Id, level));
            return team.Select(peer => AntonTcpProbe.Barrier(peer, "fiendwar dynamic death")).ToArray();
        }

        void NoDeath(ushort key, byte level, string message) =>
            That(Death(key, level).All(rows => !rows.Any(row => row is { Kind: 0, Opcode: 38 })), message);

        void Retire(ushort key, byte level)
        {
            foreach (var rows in Death(key, level))
            {
                var deaths = rows.Where(row => row is { Kind: 0, Opcode: 38 }).ToArray();
                That(deaths.Length == 1 && deaths[0].Body.Length == 9 && U16(deaths[0].Body) == key &&
                    U16(deaths[0].Body, 2) == 0, "真实动态39首次或重放仅为原对象向每名成员补一次零掉落38");
                That(!rows.Any(row => row is { Kind: 0, Opcode: 31 or 34 or 35 or 37 or 597 or 1530 }),
                    "动态退休不触发清图、结算或攻坚符号进度");
            }
        }

        string State() => string.Join('|', team.Select(peer => string.Join('|', new[]
        {
            fixture.Snapshot("SELECT revision,experience,gold,revive_coins FROM characters WHERE id=$p0", peer.Id),
            fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY event_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_dungeon_loot_receipts WHERE character_id=$p0 ORDER BY event_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_dungeon_drop_batches WHERE character_id=$p0 ORDER BY event_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_fiendwar_title_facts WHERE character_id=$p0 ORDER BY quest_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_title_progress WHERE character_id=$p0 ORDER BY quest_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY quest_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_quest_drop_receipts WHERE character_id=$p0 ORDER BY event_id", peer.Id),
            fixture.Snapshot("SELECT * FROM character_raid_runs WHERE character_id=$p0 ORDER BY instance_id", peer.Id)
        })));
    }
}
