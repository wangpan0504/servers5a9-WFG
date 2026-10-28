using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal sealed partial class TcpPeer
{
    public byte[] MarriageInformation { get; private set; } = [];
    public byte[] MarriageCapsules { get; private set; } = [];
    public byte[] MarriageRoom { get; private set; } = [];
    public byte[] MarriageStorage { get; private set; } = [];
    public List<byte[]> MarriageInvitations { get; } = [];
    public byte[] MarriageReservation { get; private set; } = [];

    WirePacket ReadMarriageInitialization(WirePacket first, ushort characterId)
    {
        MarriageInformation = []; MarriageCapsules = []; MarriageRoom = []; MarriageStorage = [];
        MarriageReservation = []; MarriageInvitations.Clear();
        if (fixture is not null && fixture.Query("SELECT character_id FROM account_pvp_private_characters WHERE character_id=$p0", characterId).Length != 0)
        {
            That(first is { Kind: 0, Opcode: 359 }, "真实PK专用角色不初始化婚姻权益，直接进入称号信息");
            return first;
        }
        That(first is { Kind: 0, Opcode: 1003, Body.Length: 15 }, "入城先按15字节真实婚姻信息覆盖前角色关系");
        MarriageInformation = first.Body;
        if (fixture is null)
        {
            Bytes(MarriageInformation, new byte[15], "未绑定账本的原生登录用例只接受完整默认婚姻信息");
            return Read();
        }
        var rows = fixture.Query("""
            SELECT r.aggregate_json FROM marriage_active_members m
              JOIN marriage_records r ON r.marriage_id=m.marriage_id WHERE m.character_id=$p0
            """, characterId);
        using var document = rows.Length == 0 ? null : JsonDocument.Parse((string)rows.Single()[0]);
        JsonElement record = document?.RootElement ?? default;
        JsonElement member = rows.Length == 0 ? default : record.GetProperty("Members").GetProperty(characterId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Bytes(MarriageInformation, rows.Length == 0 ? new byte[15] : Build(writer =>
        {
            writer.Write(record.GetProperty("ContextId").GetUInt32()); writer.Write(record.GetProperty("State").GetByte());
            writer.Write((byte)0); writer.Write(checked((uint)record.GetProperty("MarriedAt").GetInt64()));
            writer.Write(checked((uint)member.GetProperty("LovePoints").GetInt32())); writer.Write(record.GetProperty("RingLevel").GetByte());
        }), "婚姻信息逐字段来自隔离数据库，不用接收正文反推预期");
        var packet = Read();
        MarriageCapsules = []; MarriageRoom = []; MarriageStorage = []; MarriageReservation = []; MarriageInvitations.Clear();
        if (rows.Length != 0 && record.GetProperty("State").GetByte() == 4)
        {
            That(packet is { Kind: 0, Opcode: 1018, Body.Length: 7 }, "已婚入城恢复本人的胶囊计数");
            MarriageCapsules = packet.Body;
            That(U32(packet.Body, 0) == member.GetProperty("CapsuleGauge").GetInt32() &&
                packet.Body[4] == member.GetProperty("CapsulesOwned").GetInt32(), "胶囊能量与持有数不混用配偶数据");
            packet = Read();
            That(packet is { Kind: 0, Opcode: 1011 } && packet.Body.Length == 17 + U32(packet.Body, 0) * 6,
                "已婚角色在所有城镇恢复家具输入，使客户端重算婚房效果");
            MarriageRoom = packet.Body;
            packet = Read();
            var position = fixture.Query("SELECT town,area FROM characters WHERE id=$p0", characterId).Single();
            if (Convert.ToInt32(position[0]) == 101 && Convert.ToInt32(position[1]) == 0)
            {
                That(packet is { Kind: 0, Opcode: 1012 }, "婚房再恢复共享保管箱");
                MarriageStorage = packet.Body;
                That(MarriageStorage.Length == 8 + U32(MarriageStorage, 0) * 10, "婚房共用背包有独立槽位和尾字段");
                packet = Read();
            }
        }
        if (rows.Length != 0 && record.GetProperty("Wedding") is { ValueKind: JsonValueKind.Object } wedding &&
            wedding.GetProperty("State").GetByte() == 1 && wedding.GetProperty("StartsAt").GetInt64() > UnixTime)
        {
            That(packet is { Kind: 0, Opcode: 1007, Body.Length: 79 }, "未到期婚礼回放预约完整信息");
            That(U32(packet.Body, 0) == record.GetProperty("ContextId").GetUInt32() &&
                U32(packet.Body, 8) == wedding.GetProperty("StartsAt").GetInt64(), "预约标识与倒计时来自持久记录");
            MarriageReservation = packet.Body;
            packet = Read();
        }
        var invitations = fixture.Query("SELECT aggregate_json FROM marriage_records WHERE state IN (2,3)")
            .Select(row => (string)row[0]).ToArray();
        int expectedInvitations = 0;
        foreach (string json in invitations)
        {
            using var invitation = JsonDocument.Parse(json);
            var invitedWedding = invitation.RootElement.GetProperty("Wedding");
            if (invitedWedding.GetProperty("StartsAt").GetInt64() > UnixTime && invitedWedding.GetProperty("Invitations").EnumerateArray()
                .Any(row => row.GetProperty("CharacterId").GetUInt16() == characterId)) expectedInvitations++;
        }
        for (int index = 0; index < expectedInvitations; index++)
        {
            That(packet is { Kind: 0, Opcode: 1008, Body.Length: 82 }, "每份有效婚礼邀请按82字节恢复");
            MarriageInvitations.Add(packet.Body); packet = Read();
        }
        return packet;
    }
}
