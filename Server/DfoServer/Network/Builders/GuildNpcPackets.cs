using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Network.Builders;

internal static class GuildNpcPackets
{
    public static byte[] Result(GuildNpcCommit commit)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        // 005212C0仅在ACK标记非零时读取业务action/error；成功也必须提供完整8字节。
        writer.Write((byte)1); writer.Write(commit.Action);
        int error = commit.Success ? 0 : commit.Action switch { 1 => commit.Error switch { 2 => 18, 3 => 22, _ => 14 },
            2 => 95, 3 or 4 or 6 => commit.Error, _ => 14 };
        writer.Write(error); return stream.ToArray();
    }
    public static byte[] All(GuildNpcProgress[] rows)
    {
        if (rows.Length != 8 || rows.Select(row => row.NpcId).Distinct().Count() != 8)
            throw new InvalidDataException("公会NPC全量必须包含8条唯一记录");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        foreach (var row in rows.OrderBy(row => row.NpcId))
        {
            writer.Write(row.NpcId); writer.Write(row.Level);
            writer.Write(checked((int)row.UpgradeUntil)); writer.Write(checked((int)row.ContractUntil));
        }
        return stream.ToArray();
    }
    public static byte[] Benefit(GuildNpcCraftProgress row)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(row.BenefitId);
        // 00526AB0读取有符号short及两字节对齐；守护石控件只消费deadline，未消费short，不赋予猜测语义。
        writer.Write((short)0); writer.Write((ushort)0); writer.Write(checked((int)row.Deadline));
        return stream.ToArray();
    }
    public static byte[] BenefitsAll(GuildNpcCraftProgress[] rows)
    {
        if (rows.Length > 1 || rows.Any(row => row.BenefitId == 0 || row.Deadline <= 0))
            throw new InvalidDataException("守护石生产快照无效");
        // 00526260固定读取40条公有与50条个人福利，每条12字节；空索引为各段终止标志。
        byte[] body = new byte[1080];
        for (int index = 0; index < rows.Length; index++) Benefit(rows[index]).CopyTo(body, index * 12);
        return body;
    }
    public static byte[] PurchasedBenefit(GuildNpcBenefitProgress row)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(row.BenefitId); writer.Write(checked((short)row.Remaining));
        writer.Write((ushort)0); writer.Write(checked((int)row.Deadline)); return stream.ToArray();
    }
    public static byte[] BenefitsAll(GuildNpcCraftProgress[] crafts, GuildNpcBenefitState state)
    {
        byte[] body = BenefitsAll(crafts);
        if (crafts.Length + state.Common.Length > 40 || state.Individual.Length > 50)
            throw new InvalidDataException("公会福利数量超过原生固定容量");
        for (int at = 0; at < state.Common.Length; at++)
            PurchasedBenefit(state.Common[at]).CopyTo(body, (crafts.Length + at) * 12);
        for (int at = 0; at < state.Individual.Length; at++)
            PurchasedBenefit(state.Individual[at]).CopyTo(body, 480 + at * 12);
        return body;
    }
    public static byte[] Training(GuildNpcBenefitState state, GuildNpcBenefitCatalog catalog, int npcLevel)
    {
        byte[] body = new byte[16];
        for (uint id = 5001; id <= 5016; id++)
        {
            if (catalog.Find(id) is not { } definition || definition.Level > npcLevel) continue;
            var unlocked = state.Individual.SingleOrDefault(row => row.BenefitId == id);
            body[id - 5001] = unlocked is null ? (byte)1 : unlocked.Selected ? (byte)3 : (byte)2;
        }
        return body;
    }
}
