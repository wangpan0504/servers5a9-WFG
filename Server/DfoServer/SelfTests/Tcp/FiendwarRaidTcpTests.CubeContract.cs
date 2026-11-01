using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    static void VerifyCubeContractInRaid(TcpTestFixture fixture, TcpPeer peer)
    {
        string balances = fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind");
        for (byte type = 0; type < 4; type++)
        {
            Select(1, type);
            That(fixture.Query("SELECT selected,cube_type FROM cube_premium WHERE charac_no=$p0", peer.Id) is [[1L, long selected]] && selected == type,
                "单人和临时小队每名参战者均可在图内保存真实晶之契约选择");
        }
        Select(0, 255);
        That(fixture.Query("SELECT selected,cube_type FROM cube_premium WHERE charac_no=$p0", peer.Id) is [[0L, -1L]] &&
            balances == fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind"),
            "图内取消真实恢复FF；切色本身不扣晶块、不发材料，消费仍归原周期事务");

        void Select(byte selected, byte type)
        {
            peer.Send(535, [selected, type]);
            var rows = AntonTcpProbe.Barrier(peer, "fiendwar elemental contract");
            Bytes(rows.Single(row => row is { Kind: 1, Opcode: 535 }).Body, [1, selected, type],
                "漩涡实际战斗接受原生535选择，不借用被禁普通药");
            Bytes(rows.Single(row => row is { Kind: 0, Opcode: 901 }).Body, [selected, type],
                "四属性与取消均沿901回放真实选择");
            var player = rows.Single(row => row is { Kind: 0, Opcode: 2 });
            That(player.Body.Length > 24 && player.Body[0] == 1 &&
                rows.FindIndex(row => row is { Kind: 1, Opcode: 535 }) < rows.FindIndex(row => row is { Kind: 0, Opcode: 901 }) &&
                rows.FindIndex(row => row is { Kind: 0, Opcode: 901 }) < rows.IndexOf(player),
                "成功回执、选择通知、本人Player按原顺序重装虚拟装备");
        }
    }
}
