using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PreyTeamTcpTests
{
    static async Task LandDefenceTeamAsync(TcpTestFixture fixture, TcpPeer captain, TcpPeer member, TownCatalog towns,
        RaidResources resources, LukeRaidClockTests.Clock clock, Dictionary<int, int> symbols, uint key)
    {
        That(Cross(fixture, captain, towns, 5).All(row => row is not { Kind: 0, Opcode: 9 }),
            "首名成员在右侧单独等待，不提前创建普通队伍");
        ushort party = BoardingParty(Cross(fixture, member, towns, 5), 0, (0, captain.Id), (1, member.Id));
        That(BoardingParty(Read(captain, "right natural defence party"), 0, (0, captain.Id), (1, member.Id)) == party,
            "两名不同标记成员在右侧形成同一实际队伍，先到者担任实际队长");
        await FinishLandedPartyAsync(fixture, [captain, member], resources, towns, clock, symbols, key, party, [21, 11], defence: true);
    }

    static void VerifyClearedDefenceOrbit(TcpTestFixture fixture, TcpPeer[] peers, RaidResources resources, TownCatalog towns,
        LukeRaidClockTests.Clock clock, int clearedStop, int clearedDungeon, int area, int type)
    {
        int[] towers = resources.Script.Phases[0].Dungeons.Where(row => row.Declarations.Any(field =>
            field.Tag == "[type]" && field.Values is [{ Text: "magic tower" }])).Select(row => row.Id).ToArray();
        var last = new Dictionary<int, int>();
        foreach (int tower in towers)
        {
            // 真实通关两魔塔，沿原360秒暂停规则保证接下来一圈没有新的魔塔生成事件。
            Board(fixture, peers[0], towns, 4);
            var report = new AntonTcpProbe.AntonProbeReport();
            AntonTcpProbe.Enter(peers[0], resources.EntryCatalog!, tower, report, clear: true);
            last = Symbols(report.Symbols.Last());
            foreach (var peer in peers.Skip(1)) Read(peer, "tower closed before cleared defence orbit");
        }
        int speed = last[resources.Script.Symbols["[slenikon speed]"]];
        That(towers.Length == 2 && towers.All(tower => last[resources.Script.Symbols[$"[{tower} magic tower spawn time]"]] -
            clock.GetUtcNow().ToUnixTimeSeconds() > 16 * speed),
            "两塔当前原预告期限晚于完整一圈，本回归不把新魔塔生成混入已清阻击复活判断");
        string before = fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id");
        bool passed = false;
        for (int stop = 0; stop < 16; stop++)
        {
            clock.Advance(speed);
            foreach (var peer in peers)
            {
                var rows = Read(peer, "cleared defence next orbit");
                That(rows.All(row => row is not { Kind: 0, Opcode: 28 or 29 or 115 or 31 }),
                    "全员在甲板绕行时不产生新的入图、死亡许可或通关");
                var symbols = Symbols(rows.Last(row => row is { Kind: 0, Opcode: 583 }).Body);
                passed |= symbols[resources.Script.Symbols["[slenikon from area]"]] == clearedStop;
                That(symbols[area] == 0 && symbols[type] == 7,
                    "真实39清除的阻击经过后续站点仍保持空AREA及TYPE7，没有自行复活");
            }
        }
        That(passed && before == fixture.Snapshot("SELECT * FROM character_dungeon_state ORDER BY character_id") +
                fixture.Snapshot("SELECT * FROM character_raid_runs ORDER BY character_id,instance_id"),
            "实际绕回原阻击点且正式Run/攻坚贡献不新增，旧DGN不会因自然航行重新生成：" + clearedDungeon);
    }
}
