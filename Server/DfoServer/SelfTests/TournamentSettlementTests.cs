using static DfoServer.Testing.TestCheck;
using DfoServer.Network.Builders;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer;

internal static partial class TournamentSettlementTests
{
    public static void Run()
    {
        VerifyExperienceMarkers();
        VerifyRoundDeaths();
        var gold = new DungeonCardReward(1600, null, "fixture", "fixture/tournament.dgn", 0);
        var empty = gold with { Gold = 0 };
        var material = gold with { Gold = 0, Item = new(3323, ItemKind.Material, 3) };
        var cards = new DungeonTournamentReward([gold, gold], [material, empty]);
        var reward = DungeonSettlementPackets.TournamentReward(4, 1289457, cards);
        // 1 + 4 + 1 + 2 行 × (1 + 2 卡 × 10B)
        That(reward.Length == 6 + 2 * (1 + 2 * 10) && reward[0] == 4 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(reward.AsSpan(1)) == 1289457 && reward[5] == 0 &&
            reward[6] == 2 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(7)) == 0 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(11)) == 1600 &&
            reward[27] == 2 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(28)) == 3323 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(32)) == 3 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(reward.AsSpan(38)) == -1,
            "ARAD 023807C0读取轮数、经验、金币和物品；0金币/-1空牌保持独立");
        var state = DungeonSettlementPackets.TournamentSelectState(0);
        That(state.SequenceEqual(new byte[] { 1, 1, 255, 255, 255, 1, 255, 255, 255 }), "CMD457 回复为 9 字节，本人位=1其余 0xFF");
        byte[] none = [255, 255];
        That(DungeonSettlementPackets.TournamentSelection(0, cards, none).SequenceEqual(new byte[] { 1, 2, 255, 255, 2, 255, 255 }),
            "CMD458 回复未选时为 7 字节全 0xFF");
        byte[] picked = [1, 0];
        That(DungeonSettlementPackets.TournamentSelection(0, cards, picked).SequenceEqual(new byte[] { 1, 2, 255, 0, 2, 0, 255 }),
            "CMD458 回复在已选卡位写入本人位");
        DungeonRawSection Section(string tag, params int[] values) => new(tag, values.Select(value => new PvfToken(0, value)).ToArray());
        var definition = new DungeonDefinition(121, "fixture/tournament.dgn", 77, 80, [], [
            Section("tournament dungeon", 1), Section("result card"), Section("reward item rate",3323,60,1,3323,35,2,3323,5,3),
            Section("/reward item rate",4,100,0,0,5,0,100,0), Section("/result card"),
            Section("tournament clear reward exp",0,0,1,100000,2,200000,3,300000,4,500000),
            Section("tournament clear reward gold rate",16)]);
        var parsed = DungeonTournamentRewards.Parse(definition);
        That(parsed.Experience[4] == 500000 && parsed.GoldRate == 16 && parsed.Cards[4].Gold == 100 &&
            parsed.Cards[5].Item == 100 && parsed.Items.Select(row => row.Count).SequenceEqual(new[] {1,2,3}),
            "大会独立经验、嵌套物品池和普通/冠军权重均来自DGN，不回退普通装备候选");
        Reject<InvalidDataException>(() => DungeonTournamentRewards.Parse(definition with
            { RawSections = definition.RawSections.Where(row => row.Tag != "/result card").ToArray() }), "未闭合大会牌表拒绝");
    }
}
