using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Network.Builders;

internal static class ChannelPackets
{
    const string Key = "EMU-LAB-KEY-0001";
    static void Put(byte[] b, int at, int x) => BitConverter.GetBytes(x).CopyTo(b, at);
    public static byte[] Frame(int op, byte[] body)
    {
        byte[] b = new byte[11 + body.Length];
        b[0] = 0x7c;
        b[1] = (byte)op;
        Put(b, 2, b.Length);
        b[10] = 1;
        body.CopyTo(b, 11);
        return b;
    }

    public static byte[] Hello()
    {
        byte[] b = new byte[36];
        Encoding.ASCII.GetBytes(Key).CopyTo(b, 4);
        return Frame(12, b);
    }

    public static byte[] Script(byte[] script) => Frame(10, Encrypt(script));
    public static byte[] Directory(string address, IReadOnlyList<ChannelEndpoint> endpoints)
    {
        if (endpoints.Count == 0) throw new InvalidDataException("频道监听尚未就绪");
        byte[] b = new byte[12 + 64 * endpoints.Count];
        Put(b, 0, 1);
        Put(b, 4, endpoints[0].Entry.Identity.ServerIndex);
        Put(b, 8, endpoints.Count);
        byte[] ip = Encoding.ASCII.GetBytes(address);
        for (int i = 0; i < endpoints.Count; i++)
        {
            int at = 12 + 64 * i;
            Encoding.ASCII.GetBytes("#ch." + endpoints[i].Entry.Identity.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).CopyTo(b, at);
            // 频道目录条目的 +0x14 是原生容量，+0x18 是当前在线人数；空入口保持 50/0，客户端显示“流畅”。
            Put(b, at + 20, 50);
            Put(b, at + 24, 0);
            ip.CopyTo(b, at + 28);
            Put(b, at + 60, endpoints[i].Port);
        }
        return Frame(18, Encrypt(b));
    }

    static byte[] Encrypt(byte[] plain)
    {
        byte[] padded = new byte[(plain.Length + 15) / 16 * 16];
        plain.CopyTo(padded, 0);
        byte[] key = new byte[16];
        Encoding.ASCII.GetBytes(Key).CopyTo(key, 0);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        byte[] encrypted = aes.EncryptEcb(padded, PaddingMode.None);
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, true))
            z.Write(encrypted);
        return output.ToArray();
    }

    // CHANNEL_EVILDOM_WAR：ARAD 频道类型名表 03EFBF1D 写入 0x36。
    const int EvildomWarType = 0x36;
    // 0233EF02原样写入0x40，028FA2F0独立判断英雄频道，不能替换为普通战役0x36。
    const int EvildomWarChallengeType = 0x40;
    // CHANNEL_FIEND_WAR：超时空之战频道 39 [fiendwar]。
    const int FiendWarType = 0x2D;
    // CHANNEL_PREY_RAID：普雷频道 42 [prey]。选角前 03B55580 经 02330DF0 读 +0x1010，cmp 0x32 → 名册 +0x494（tail[41]），为 0 弹 1099 / dstr 32116；
    // RaidModeSelectWindow 014C0D40 按 +0x1010-0x20 跳表，0x32 → 信息框下标 2；ACK LOGIN 处理器的 03EF95B0 改写列表不含 0x32，原样写入。
    const int PreyRaidType = 0x32;
    const int AntonRaidType = 0x17;
    const int LukeRaidType = 0x20;
    // 再战魔界：028FA250按+1010==0x35选择双角色UI和9608/400000095；该字段由ACK LOGIN类型写入。
    const int EvildomGatheringType = 0x35;
    // CHANNEL_DPL_PVE：03EFBF5E登记0x37，moveDPLPVEChannel经016EA08B选择该类型；ACK LOGIN保留真实模式。
    const int DplPveType = 0x37;

    // ACK LOGIN 正文第 0 字节是结果，之后 ARAD 0233EA10 依次读 u8 年龄 [ebp-0x34b]、u8 网吧 [ebp-0x347]、u8 频道类型 [ebp-0x336]；
    // 频道类型经 0233EF02 → 028FA920 写入频道对象 +0x1010（02330DF0 读出），RaidModeSelectWindow 打开时 014C0D40 按它选信息框下标：
    // 0x36 → 3（Lv95、4 人、+12 史诗武器、抗魔 5382、疲劳 30，表 0530C088..0530C0D8），其余未列类型 → 4，填字函数 014BF460 遇 4 直接跳过，
    // 信息框只剩 xui 占位字“Lv.00 ~ 00 / 0人以上 / +00”，创建按钮也从“创建队伍”(21252) 变成“确定”(300000975)。
    // 0x2D（超时空之战 CHANNEL_FIEND_WAR）同样发真实类型：+0x1010 在客户端有 221 处读点（02330DF0 调用点），紧接着与频道类型枚举常量比较
    // （0x32×28、0x13×23、0x2D×18、0x20×17、0x17×14、0x36×5 …），014C0D40/02378D97 都按 0x20/0x2D/0x32 成组分支，03EF9610 判 {0x17,0x20,0x2D,0x32}；
    // ACK LOGIN 处理器只把 03EF95B0 列表内的 5 改 4、0x24 另调 010686E0，0x2D 原样写入。证据：evidence/ack-login-channel-type-readers-20261021.txt。
    // CHANNEL_ANTON_RAID 0x17（频道 19）/ CHANNEL_LUKE_RAID 0x20（频道 20）同样发真实类型：选角前 03B55580 按 0x17 → 名册 +0x491（tail[37]）、
    // 0x20 → +0x492（tail[38]）判资格，发 0 时资格位不生效。逐个核对 43 处 0x17/0x20 读点：团本创建框/团本管理器类型表、频道类型 → 城镇
    // （03B876C0：0x17→19、0x20→32，与服务端落点一致）、团队频道内跳过若干普通频道 UI/提示、卢克频道快捷窗 0x5C9 与物品频道限制谓词 022C4430；
    // 页签与换频道快照谓词读脚本类型（+8），不读 +0x1010。证据：evidence/anton-luke-channel-type-audit-20261021.txt。
    // 03EF95B0/028FA230将4纳入普通决斗准入；01714BC0/01714BCD：8显示无双决斗场，24显示传统决斗场；LOGIN均保留实际类型。
    // 普通type22用于冒险团页准入（012AE420→028FB170）；其他未确认入口继续发0，避免扩大客户端模式变化。
    public static byte LoginChannelType(ChannelEntry channel) =>
        channel.Type is 4 or 5 or 8 or 13 or 24 or 22 or 39 or AntonRaidType or LukeRaidType or EvildomWarType or EvildomWarChallengeType or FiendWarType or PreyRaidType or EvildomGatheringType or DplPveType ? (byte)channel.Type : (byte)0;

    public static byte[] Endpoint(string address, int port, uint association = 0, byte channelType = 0)
    {
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s);
        w.Write(new byte[] { 1, 1, 1, channelType, 1 });
        w.Write(association);
        byte[] ip = Encoding.ASCII.GetBytes(address);
        w.Write(ip.Length);
        w.Write(ip);
        w.Write(port);
        w.Write(0u);
        w.Write(new byte[] { 0, 0, 1, 1, 0, 0, 0 });
        w.Write(new byte[4]);
        w.Write(0u);
        w.Write(0u);
        w.Write((ushort)0);
        w.Write((byte)0);
        w.Write((byte)0);
        return s.ToArray();
    }
}
