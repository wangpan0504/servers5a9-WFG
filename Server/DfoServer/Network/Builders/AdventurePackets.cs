using System.Buffers.Binary;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Network.Builders;

internal static class AdventurePackets
{
    public const int InformationRawLength = 7440;
    public const int RefreshRawLength = 40;
    public const int RosterOffset = 72;
    public const int ShopOffset = 5272;
    public const int PurchasesOffset = 5312;
    public const int ProfessionsOffset = 7360;
    public const int CapsuleOffset = 7424;
    public const int ClaimsOffset = 7432;

    public static byte[] Ack(ushort error = 0) => Build(writer =>
    {
        writer.Write(error == 0 ? (byte)1 : (byte)0);
        if (error != 0) writer.Write(error);
    });
    public static byte[] Information(ushort actor, AdventureSnapshot snapshot, AdventureCatalog catalog,
        IReadOnlyList<AdventureMember> representatives, byte region, bool ack = false) => Build(writer =>
    {
        if (ack) writer.Write((byte)1);
        writer.Write(actor); writer.Write(checked((uint)snapshot.State.CreatedDate));
        writer.Write(new byte[4]); writer.Write(0u);
        var raw = new byte[InformationRawLength];
        U32(raw, 0, checked((uint)snapshot.State.Level)); U64(raw, 8, snapshot.State.Experience);
        U16(raw, 68, checked((ushort)snapshot.Members.Length));
        int at = 0;
        foreach (var profession in catalog.Professions.Values.OrderBy(row => row.Job).ThenBy(row => row.Grow))
        {
            var member = representatives.SingleOrDefault(row => row.Job == profession.Job && row.ProfessionGrow == profession.Grow);
            if (member is null && !snapshot.State.Representatives.Any(row => row.Job == profession.Job && row.Grow == profession.Grow)) continue;
            var row = raw.AsSpan(RosterOffset + 52 * at++, 52);
            row[0] = profession.Job; row[1] = profession.Grow;
            if (member is not null) WriteCollection(row, member, region);
        }
        for (int i = 0; i < 5; i++)
        {
            U32(raw, ShopOffset + i * 8, snapshot.State.Points[i]);
            if (i == 0) U32(raw, ShopOffset + 4, checked((uint)snapshot.State.BraveExperience));
        }
        for (byte group = 0; group < 20; group++)
        {
            raw[ProfessionsOffset + group * 3] = group;
            U16(raw, ProfessionsOffset + group * 3 + 1, checked((ushort)snapshot.Members.Count(row =>
                catalog.Eligible(row.Job, row.Grow, row.Level) && catalog.Professions[(row.Job, row.ProfessionGrow)].Group == group)));
        }
        U64(raw, CapsuleOffset, snapshot.State.CapsuleExperience);
        U32(raw, ClaimsOffset, Claims(snapshot.State, catalog));
        writer.Write(raw.Length); writer.Write(raw); PacketEncoding.WriteString(writer, snapshot.State.Name);
    });

    public static byte[] Refresh(ushort actor, AdventureState state, AdventureCatalog catalog) => Build(writer =>
    {
        writer.Write(actor);
        var raw = new byte[RefreshRawLength];
        U32(raw, 0, checked((uint)state.Level)); U64(raw, 8, state.Experience);
        U64(raw, 24, state.CapsuleExperience); U32(raw, 32, Claims(state, catalog));
        writer.Write(raw.Length); writer.Write(raw); writer.Write(new byte[4]);
    });
    public static uint Claims(AdventureState state, AdventureCatalog catalog) => checked((uint)(state.CapsuleExperience / catalog.Capsule.MinimumExperience));

    public static byte[] Candidates(IReadOnlyList<AdventureMember> rows, byte region) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(rows.Count == 0 ? (byte)0 : (byte)1);
        if (rows.Count == 0) return;
        writer.Write(region); writer.Write(checked((byte)rows.Count));
        foreach (var member in rows)
        {
            var row = new byte[52]; row[0] = region;
            U32(row, 4, member.Proficiency); U32(row, 8, member.Level); U32(row, 12, member.Id);
            Name(row.AsSpan(20, 30), member.Name); writer.Write(row);
        }
    });
    public static byte[] Representative(AdventureProfession profession, ushort count, AdventureMember member, byte region) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(3u); writer.Write(profession.Group); writer.Write(count);
        var row = new byte[52]; WriteCollection(row, member, region);
        writer.Write(52u); writer.Write(row);
    });

    static void WriteCollection(Span<byte> row, AdventureMember member, byte region)
    {
        row[0] = member.Job; row[1] = member.ProfessionGrow;
        U32(row, 4, member.Proficiency); Name(row.Slice(12, 30), member.Name);
        row[42] = region; U32(row, 48, member.Level);
    }


    public static byte[] Expedition(AdventureSnapshot snapshot, DfoServer.GameWorld.Adventure.AdventureExpeditionCatalog catalog, IReadOnlyList<AdventureMember> representatives,
        IReadOnlyDictionary<byte, byte[]> attributes, DateTimeOffset now, bool ack = true) => Build(writer =>
    {
        // NOTI同步和ACK441均先读一个success/状态字节，再消费同一远征信息列表。
        if (ack) writer.Write((byte)1);
        writer.Write(checked((byte)(catalog.Areas.Count + 1)));
        WriteArea(0, null, [], representatives.Select((row, selector) => new ExpeditionMember(row.Id, checked((byte)selector),
            row.Name, row.Job, row.ProfessionGrow, row.Level)).Where(row => !snapshot.State.Expeditions.Any(run => run.Members.Contains(row.Id))).ToArray());
        foreach (var area in catalog.Areas.Keys.Order())
        {
            var run = snapshot.State.Expeditions.SingleOrDefault(row => row.Area == area);
            WriteArea(area, run, run?.Attributes.ToArray() ?? attributes[area], run is null ? [] :
                run.FrozenMembers.ToArray());
        }
        void WriteArea(byte area, AdventureExpedition? run, byte[] desired, IReadOnlyList<ExpeditionMember> members)
        {
            writer.Write(area); writer.Write(run is null ? (byte)0 : run.Ends <= now.ToUnixTimeSeconds() ? (byte)2 : (byte)1);
            writer.Write(checked((uint)(run?.Started ?? 0))); writer.Write(checked((uint)(run?.Ends ?? 0)));
            writer.Write(checked((byte)desired.Length)); writer.Write(desired);
            writer.Write(checked((ushort)members.Count));
            foreach (var member in members)
            {
                writer.Write((ushort)member.Selector); PacketEncoding.WriteString(writer, member.Name);
                writer.Write((uint)member.Level); writer.Write(member.Job); writer.Write(member.Grow); writer.Write((byte)0);
            }
        }
    });
    public static byte[] ExpeditionReward(uint reward) => Build(writer => { writer.Write((byte)1); writer.Write(reward); });

    static void Name(Span<byte> target, string name)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        if (bytes.Length >= target.Length) throw new InvalidDataException("冒险团角色名超过固定30B字段");
        bytes.CopyTo(target);
    }
    static void U16(Span<byte> data, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data[at..], value);
    static void U32(Span<byte> data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[at..], value);
    static void U64(Span<byte> data, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data[at..], value);
    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        write(writer); return stream.ToArray();
    }
}
