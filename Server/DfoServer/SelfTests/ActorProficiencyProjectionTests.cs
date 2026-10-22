using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ActorProficiencyProjectionTests
{
    public static void RunCore()
    {
        int checks = Count;
        // 026768F0 在 Actor 尾部依次读取熟练等级u32与当前级经验u64，客户端经 01A16420/01A16470
        // 写角色 Data+42C/+430/+434，左下角HUD与「熟练Lv」窗口直接消费这三个字段。
        // 满级停发NOTI37后没有其他同步通道，因此这里核对的是持久化状态的投影，而不是写0的结构占位。
        static Character Actor(uint level, ulong experience, bool owner = true) => new(1, 0, "ProficiencyActor", 0,
            new(0, 0, 0, 0), null)
        {
            DungeonState = owner
                ? CharacterDungeonState.NewOrdinary with { ProficiencyLevel = level, ProficiencyExperience = experience }
                : null
        };
        byte[] low = TownPackets.Actor(Actor(137, 9_876_543_210UL));
        byte[] high = TownPackets.Actor(Actor(138, 9_876_543_210UL));
        That(low.Length == high.Length, "熟练等级不改变Actor长度");
        int[] changed = Enumerable.Range(0, low.Length).Where(index => low[index] != high[index]).ToArray();
        // 等级与经验是相邻的12字节，除熟练等级自身的字节外不得影响任何后续字段。
        That(changed.Length == 1 && changed[0] == low.Length - 35,
            "熟练等级只占尾部相邻u32的1字节，不覆盖经验、显示位与固定字段");
        That(BinaryPrimitives.ReadUInt32LittleEndian(low.AsSpan(low.Length - 35)) == 137 &&
            BinaryPrimitives.ReadUInt64LittleEndian(low.AsSpan(low.Length - 31)) == 9_876_543_210UL,
            "Actor尾部依次投影持久化熟练等级u32与当前级经验u64");
        byte[] empty = TownPackets.Actor(Actor(0, 0, owner: false));
        That(BinaryPrimitives.ReadUInt32LittleEndian(empty.AsSpan(empty.Length - 35)) == 0 &&
            BinaryPrimitives.ReadUInt64LittleEndian(empty.AsSpan(empty.Length - 31)) == 0,
            "缺少附属owner的角色熟练等级与经验保持0，不伪造进度");
        // USERINFO subtype1 本人Player紧随Actor到达；0267C4F0 读17字节头，02676970 再无条件覆盖
        // Data+42C/+430/+434。两包必须投影同一份熟练状态，否则HUD被清成「挑战者 0」。
        var stats = new CharacterStats(8550, 5150, 123, 45, 67, 89, -10, 20, -30, 40,
            480000, 525, 800, 100, 100, 6000, 100, 600);
        var owner = Actor(188, 871_311_064UL);
        byte[] player = TownPackets.Player(owner, stats, 1);
        That(player[5] == 0 && player[6] == 0 && player[19] == 0 && player[20] == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(player.AsSpan(7)) == 188 &&
            BinaryPrimitives.ReadUInt64LittleEndian(player.AsSpan(11)) == 871_311_064UL,
            "Player 17字节头在bytes2-5/bytes6-13投影熟练等级与当前级经验，首尾保留字节为0");
        byte[] actor = TownPackets.Actor(owner);
        That(BinaryPrimitives.ReadUInt32LittleEndian(actor.AsSpan(actor.Length - 35)) ==
                BinaryPrimitives.ReadUInt32LittleEndian(player.AsSpan(7)) &&
            BinaryPrimitives.ReadUInt64LittleEndian(actor.AsSpan(actor.Length - 31)) ==
                BinaryPrimitives.ReadUInt64LittleEndian(player.AsSpan(11)),
            "连续两包Actor与Player携带同一份熟练状态，Player不再覆盖成挑战者0");
        That(TownPackets.Player(Actor(188, 871_311_064UL, owner: false), stats, 1).AsSpan(5, 16).SequenceEqual(new byte[16]),
            "缺少附属owner时Player头保持0，不伪造进度");
        Console.WriteLine($"PASS {Count - checks} Actor/Player熟练度投影断言");
    }
}
