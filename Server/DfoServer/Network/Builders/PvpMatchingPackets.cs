using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;

namespace DfoServer.Network.Builders;

internal sealed record PvpTotalRoundSelection(uint OwnerCharacter, byte CharacterIndex);
// 未闭合的显示字段保留原值；不把旧赛季字段误称为当前段位或补造评分。
internal sealed record PvpMatchingPlayer(ushort Character, byte Side, uint FirstValue, byte BadgeValue,
    uint SecondValue, byte OverrideValue);

internal static class PvpMatchingPackets
{
    // NOTI504非总力分支：6B头部＋每人13B。mode8还需三角色摘要，不经过这个构包器。
    public static byte[] Players(byte mode, uint id, ImmutableArray<PvpMatchingPlayer> players,
        ImmutableArray<PvpTotalBattleRoster> totals = default, IReadOnlyDictionary<ushort, PvpRatingSnapshot>? ratings = null)
    {
        if (mode == 8 && (totals.IsDefault || totals.Length != 2 || players.Length != 2 || ratings is null) ||
            mode != 8 && !totals.IsDefaultOrEmpty || id == 0 || players.IsDefaultOrEmpty || players.Length > byte.MaxValue ||
            players.Any(row => row.Character is 0 or ushort.MaxValue) ||
            players.Select(row => row.Character).Distinct().Count() != players.Length)
            throw new InvalidDataException("匹配名单标识或角色无效，或缺少总力摘要");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(mode);
        writer.Write(id);
        writer.Write((byte)players.Length);
        foreach (var row in players)
        {
            writer.Write(row.Character);
            writer.Write(row.Side);
            writer.Write(row.FirstValue);
            writer.Write(row.BadgeValue);
            writer.Write(row.SecondValue);
            writer.Write(row.OverrideValue);
            if (mode == 8) PvpTotalPackets.Summary(writer,
                totals.Single(total => total.Selection.Actor.Character.Id == row.Character), ratings![row.Character]);
        }
        return stream.ToArray();
    }

    // NOTI505消费者不读正文；NOTI506单u8中2推进加载，1/3清理并显示各自提示。
    public static byte[] BeginLoading() => [];
    public static byte[] LoadingState(byte value) => value is 1 or 2 or 3 ? [value] :
        throw new InvalidDataException("匹配加载状态未闭合");

    // ACK520的成功字节之后仍有四字节，不能用只有01的通用成功替代。
    public static byte[] Accepted(byte kind, ushort gameType, byte flag) =>
        [1, kind, (byte)gameType, (byte)(gameType >> 8), flag];

    public static byte[] Cancelled() => [1];
    public static byte[] Rejected(ushort error) => [0, (byte)error, (byte)(error >> 8)];

    // NOTI460与ACK520类别不同；这里保留两个原值，不借模式名猜测关闭原因。
    public static byte[] Closed(byte kind, byte value) => [kind, value];

    public static byte[] TeamAccepted(ImmutableArray<byte> indices) => [1, ..Indices(indices)];

    // NOTI804在场景Actor已经存在时安装队伍；三个值沿原客户端列表行序。
    public static byte[] Team(string name, ImmutableArray<byte> indices)
    {
        byte[] characters = Indices(indices);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteName(writer, name);
        writer.Write(characters);
        return stream.ToArray();
    }

    public static byte[] NameChanged(string name)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        WriteName(writer, name);
        return stream.ToArray();
    }

    // NOTI806的每个身份字段由03C85290读取四字节，不能沿普通席位写成u16。
    public static byte[] RoundSelection(ImmutableArray<PvpTotalRoundSelection> selections)
    {
        if (selections.IsDefault || selections.Length != 2 || selections.Any(row => row.OwnerCharacter == 0 || row.CharacterIndex > 2) ||
            selections[0].OwnerCharacter == selections[1].OwnerCharacter)
            throw new InvalidDataException("总力回合必须包含两个不同身份和有效选人索引");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var selection in selections)
        {
            writer.Write(selection.OwnerCharacter);
            writer.Write(selection.CharacterIndex);
        }
        return stream.ToArray();
    }

    static byte[] Indices(ImmutableArray<byte> indices)
    {
        if (indices.IsDefault || indices.Length != 3) throw new InvalidDataException("总力队伍必须保留三个索引字段");
        return indices.ToArray();
    }

    static void WriteName(BinaryWriter writer, string name)
    {
        if (name.Length > 31 || name.Any(char.IsControl)) throw new InvalidDataException("总力队名超出客户端容量或含控制字符");
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        if (bytes.Length > 62) throw new InvalidDataException("总力队名字节数超出客户端容量");
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }
}
