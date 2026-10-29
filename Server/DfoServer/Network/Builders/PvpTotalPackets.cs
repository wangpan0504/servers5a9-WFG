using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal static class PvpTotalPackets
{
    public static void Summary(BinaryWriter writer, PvpTotalBattleRoster roster, PvpRatingSnapshot rating)
    {
        PacketEncoding.WriteString(writer, roster.Team.Name);
        foreach (var actor in roster.Characters)
        {
            PacketEncoding.WriteString(writer, actor.Name);
            writer.Write(actor.Job);
            writer.Write(actor.GrowType);
            writer.Write(actor.IsPvpPrivate ? (byte)1 : (byte)0);
        }
        writer.Write(checked((uint)rating.State.Rating));
    }

    public static byte[] Summaries(ImmutableArray<PvpTotalBattleRoster> rosters,
        IReadOnlyDictionary<ushort, PvpRatingSnapshot> ratings)
    {
        Validate(rosters);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)rosters.Length);
        foreach (var roster in rosters)
        {
            ushort owner = roster.Selection.Actor.Character.Id;
            writer.Write((uint)owner);
            Summary(writer, roster, ratings[owner]);
        }
        return stream.ToArray();
    }

    public static byte[] Actors(ImmutableArray<PvpTotalBattleRoster> rosters,
        Func<Character, DfoServer.GameWorld.Characters.CharacterStats> stats)
    {
        Validate(rosters);
        return WriteActors(rosters, stats);
    }

    // 023B4603的count1分支专门刷新大厅UI；开战前须先为本人安装三名真实队员。
    public static byte[] OwnActors(PvpTotalBattleRoster roster,
        Func<Character, DfoServer.GameWorld.Characters.CharacterStats> stats)
    {
        ImmutableArray<PvpTotalBattleRoster> rosters = [roster];
        Validate(rosters, 1);
        return WriteActors(rosters, stats);
    }

    static byte[] WriteActors(ImmutableArray<PvpTotalBattleRoster> rosters,
        Func<Character, DfoServer.GameWorld.Characters.CharacterStats> stats)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)rosters.Length);
        foreach (var roster in rosters)
        {
            writer.Write(roster.Selection.Actor.Character.Id);
            writer.Write((byte)3);
            for (byte i = 0; i < 3; i++)
            {
                var actor = roster.Characters[i];
                writer.Write(i);
                PacketEncoding.WriteString(writer, actor.Name);
                writer.Write(actor.Level);
                writer.Write(actor.Job);
                writer.Write(actor.GrowType);
                // 026768A0的两字节及u32辅助值没有持久来源，兼容投影为中立值。
                writer.Write((ushort)0);
                writer.Write(0u);
                writer.Write(actor.IsPvpPrivate ? (byte)1 : (byte)0);
                var attributes = CharacterStatsPackets.Build(stats(actor));
                writer.Write(attributes.Length);
                writer.Write(attributes);
                writer.Write(actor.EquipmentSlotFlags);
                writer.Write((byte)((actor.EquipmentSlotFlags & 32) == 0 ? 0 : 1));
                writer.Write(0u); // special=1的独立辅助标量，不能写个人或队伍评分。
                EquipmentProtocolWriter.Appearance(writer, actor.Inventory);
                writer.Write(actor.CloneTitleItemId);
                writer.Write(0u); // 02675FF0/02676020/023CF380及独立u8。
                // 023C3A90与本人Player共用完整装备读取，技能换装属于外部0267BFA0，此处不存在。
                EquipmentProtocolWriter.Player(writer, actor.Inventory);
                writer.Write(actor.CloneTitleItemId);
                EquipmentProtocolWriter.Runes(writer, actor.Inventory);
                writer.Write((ushort)0); // 技能对象的独立辅助值，保留构造初始状态。
                actor.Learning.Validate();
                var tree = actor.Learning.Trees.Single(row => row.Context == actor.Learning.ActiveContext);
                writer.Write(checked((byte)tree.Skills.Count));
                writer.Write(actor.Learning.ActiveContext);
                foreach (var skill in tree.Skills.OrderBy(row => row.Slot))
                {
                    writer.Write(skill.Slot);
                    writer.Write(skill.Id);
                    writer.Write(skill.Level);
                }
                writer.Write(0u); // 02676510辅助列表。
                for (int field = 0; field < 5; field++) writer.Write(0u);
                EquipmentProtocolWriter.NameTag(writer, actor.Inventory);
                writer.Write(0UL); // special=1末尾原始8B，未将未知结构误认作评分或奖励。
            }
        }
        return stream.ToArray();
    }

    static void Validate(ImmutableArray<PvpTotalBattleRoster> rosters, int count = 2)
    {
        if (rosters.IsDefault || rosters.Length != count || rosters.Any(row => !row.Team.Complete || row.Characters.Length != 3 ||
            !row.Team.Characters.SequenceEqual(row.Characters.Select(actor => (ushort?)actor.Id))) ||
            rosters.SelectMany(row => row.Characters).Select(row => row.Id).Distinct().Count() != count * 3)
            throw new InvalidDataException("总力演员必须属于指定数量的真实三角色队伍");
    }
}
