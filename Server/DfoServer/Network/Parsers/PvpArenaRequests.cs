using System.Buffers.Binary;
using DfoServer.Game.Pvp;

namespace DfoServer.Network.Parsers;

internal static class PvpArenaRequests
{
    // 0395D378：预设非零时省略标题；密码标志为1时才附带长度和密码。
    // 训练及普通入口共用CMD50外壳，玩法准入由各自用例决定。
    public static PvpRoomRequest Create(ReadOnlySpan<byte> body)
    {
        var reader = new SocialRequests.Reader(body);
        byte preset = reader.Byte();
        if (preset > 16) throw new InvalidDataException("决斗房间标题预设越界");
        string title = preset == 0 ? reader.String(63) : "";
        if (preset == 0 && string.IsNullOrWhiteSpace(title))
            throw new InvalidDataException("决斗房间标题为空");
        ushort map = reader.Word();
        byte setting = reader.Byte();
        if (setting > 1) throw new InvalidDataException("决斗房间密码标志无效");
        string password = setting == 1 ? reader.String(4096) : "";
        if (setting == 1 && password.Length == 0) throw new InvalidDataException("决斗房间密码为空");
        var request = new PvpRoomRequest(title, map, setting, reader.Byte(), reader.Byte())
        { TitlePreset = preset, Password = password };
        reader.End();
        return request;
    }

    // 0395DFB1/0395DFFC发标志0；038CC320密码回调发标志1并附u32长度及CP936。
    public static PvpRoomEnterRequest Enter(ReadOnlySpan<byte> body)
    {
        var reader = new SocialRequests.Reader(body);
        ushort room = reader.Word();
        if (room is 0 or ushort.MaxValue) throw new InvalidDataException("普通决斗房间编号无效");
        byte flag = reader.Byte();
        if (flag > 1) throw new InvalidDataException("决斗入房密码标志无效");
        string? password = flag == 1 ? reader.String(4096) : null;
        if (password == "") throw new InvalidDataException("决斗入房密码为空");
        reader.End();
        return new(room, password);
    }

    public static bool Ready(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1 || body[0] > 1) throw new InvalidDataException("决斗准备请求无效");
        return body[0] != 0;
    }

    public static byte Mode(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1) throw new InvalidDataException("决斗模式请求长度错误");
        return body[0];
    }

    public static ushort Map(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2) throw new InvalidDataException("决斗地图请求长度错误");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }

    public static void Empty(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty) throw new InvalidDataException("决斗请求不应携带正文");
    }

    // 03BD22FB按原席位顺序写八个u32；空席也保留，不能按当前人数截短。
    public static uint[] Timeout(ReadOnlySpan<byte> body)
    {
        if (body.Length != 32) throw new InvalidDataException("决斗超时报告必须包含八席数据");
        var values = new uint[8];
        for (int seat = 0; seat < values.Length; seat++)
            values[seat] = BinaryPrimitives.ReadUInt32LittleEndian(body[(seat * 4)..]);
        return values;
    }

    // 03B82E6F在包头后直接发送，返回结果窗口不携带席位或局ID。
    public static void ResultEnd(ReadOnlySpan<byte> body) => Empty(body);

    // 03B7C610始终按八席写seat/flag；1表示没有待连接peer，0不能当作已连通。
    public static bool[] Connections(ReadOnlySpan<byte> body)
    {
        if (body.Length != 17 || body[0] != 8) throw new InvalidDataException("决斗连接列表长度错误");
        var seats = new bool[8];
        byte seen = 0;
        for (int at = 1; at < body.Length; at += 2)
        {
            byte seat = body[at], flag = body[at + 1];
            if (seat > 7 || flag > 1 || (seen & 1 << seat) != 0) throw new InvalidDataException("决斗连接席位或标志无效");
            seen |= (byte)(1 << seat);
            seats[seat] = flag == 1;
        }
        return seats;
    }

    // 02B46860：本人UID、击杀者UID、来源标志，以及两个固定保留字段。
    public static (ushort Victim, ushort Killer) Death(ReadOnlySpan<byte> body)
    {
        if (body.Length != 11 || body[4] > 1 || BinaryPrimitives.ReadUInt16LittleEndian(body[5..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[7..]) != 0) throw new InvalidDataException("决斗死亡报告无效");
        return (BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]));
    }
}
