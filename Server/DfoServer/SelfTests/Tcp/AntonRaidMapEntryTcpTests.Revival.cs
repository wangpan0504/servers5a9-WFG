using DfoServer.Game.Session;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class AntonRaidMapEntryTcpTests
{
    // 复用真实START/15/16/37后的210双成员Run，覆盖原入口曾强制CoinLimit=0的回归。
    static void VerifyFormalCoins(TcpTestFixture fixture, GameSession[] sessions, TcpPeer[] peers)
    {
        foreach (var session in sessions)
            That(session.MutateInventory(character => new(true, 0,
                new(character.Inventory, character.Inventory with { ReviveCoins = 20 }, "anton_formal_coins")),
                requireTown: false).Accepted, "正式双成员Run准备本人钱包");
        var shared = sessions[0].PartyDungeon!;
        for (int used = 0; used < 12; used++)
        {
            int index = used % 2, other = 1 - index;
            ushort key = sessions[index].Selected!.Id;
            peers[index].Send(40, [0, 0, 0, 0]);
            var ownDeath = Read(peers[index]); var partnerDeath = Read(peers[other]);
            That(new[] { ownDeath, partnerDeath }.All(rows => rows.Count(row =>
                row is { Kind: 0, Opcode: 32 } && TcpPeer.U16(row.Body) == key && row.Body[2] == 0) == 1),
                "正式210死亡向原分队发送state0");
            if (used == 0)
            {
                fixture.Sql("CREATE TRIGGER fail_anton_coin BEFORE UPDATE OF revive_coins ON characters BEGIN SELECT RAISE(ABORT,'Anton coin rollback'); END");
                Reject<SqliteException>(() => sessions[index].ReviveFormalCharacter(key), "正式复活钱包失败回滚共享额度");
                fixture.Sql("DROP TRIGGER fail_anton_coin");
                That(sessions[index].Selected!.Inventory.ReviveCoins == 20 && sessions[index].CurrentDungeonDeathReturn() is not null,
                    "钱包回滚保留死亡与原币数，原41可重试");
            }
            peers[index].Send(41, [(byte)key, (byte)(key >> 8)]);
            var own = Read(peers[index]); var partner = Read(peers[other]);
            That(own.Any(row => row is { Kind: 1, Opcode: 41 } && row.Body.SequenceEqual(new byte[] { 1, (byte)key, (byte)(key >> 8), 0 })) &&
                new[] { own, partner }.All(rows => rows.Any(row => row is { Kind: 0, Opcode: 32 } && row.Body[2] == 1)) &&
                sessions[index].Selected!.Inventory.ReviveCoins == 19 - used / 2,
                "正式210原41成功只扣本人钱包并向同分队复活");
            var hp = shared.ReportAntonHp(sessions[index], 17, [73]);
            That(hp.Length == 2 && hp.All(row => row.AntonHp!.UsedCoins == used + 1),
                "共享634投影严格等于已提交总消费，SQL失败不占计数");
            peers[index].Send(41, [(byte)key, (byte)(key >> 8)]);
            That(Read(peers[index]).All(row => row.Opcode != 41) && Read(peers[other]).All(row => row.Opcode != 32),
                "重复41不重复扣币或发布复活");
        }
        ushort exhausted = sessions[0].Selected!.Id;
        peers[0].Send(40, [0, 0, 0, 0]); Read(peers[0]); Read(peers[1]);
        peers[0].Send(41, [(byte)exhausted, (byte)(exhausted >> 8)]);
        var rejected = Read(peers[0]); var others = Read(peers[1]);
        That(rejected.Any(row => row is { Kind: 1, Opcode: 41 } && row.Body.SequenceEqual(new byte[] { 0, 1, 0 })) &&
            rejected.Concat(others).All(row => row.Opcode != 14 && !(row is { Kind: 0, Opcode: 32 } && row.Body[2] == 1)) &&
            sessions[0].Selected!.Inventory.ReviveCoins == 14 && sessions[0].CurrentDungeonDeathReturn() is not null,
            "两成员共享12次，第13次即使本人有币也拒绝，保留钱包与死亡");
        // 额度验证结束后注入关联丢失；原个人计数仅6，不能降级到普通副本再扣一次。
        sessions[0].RemovePartyDungeon(shared);
        That(sessions[0].ReviveFormalCharacter(exhausted) is { Accepted: false } &&
            sessions[0].Selected!.Inventory.ReviveCoins == 14 && sessions[0].CurrentDungeonDeathReturn() is not null,
            "正式安徒恩缺失共享实例时明确拒绝，不能绕过全队额度使用个人计数");
    }
}
