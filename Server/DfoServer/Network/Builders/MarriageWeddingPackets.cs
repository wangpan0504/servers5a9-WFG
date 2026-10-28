using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

// 尚未证明的控制字段由调用方显式传入，不把零值拼成虚假的成功状态。
internal readonly record struct MarriageCeremonyWire(uint ContextRaw, uint CeremonyRaw, uint Timestamp,
    uint FourthRaw, byte Type, string FirstName, string SecondName);

internal static class MarriageWeddingPackets
{
    public static byte[] RequestResult(int result, uint target, byte operation, byte extraA = 0, byte extraB = 0) =>
        Build(writer => WriteRequest(writer, result, target, operation, extraA, extraB));

    public static byte[] ResponseResult(int result, uint target, byte operation, byte response,
        byte extraA = 0, byte extraB = 0) => Build(writer =>
    {
        WriteRequest(writer, result, target, operation, extraA, extraB);
        writer.Write(response);
    });

    public static byte[] RequestAck(int result, uint target, byte operation, byte extraA = 0, byte extraB = 0) =>
        Ack(RequestResult(result, target, operation, extraA, extraB));

    public static byte[] ResponseAck(int result, uint target, byte operation, byte response,
        byte extraA = 0, byte extraB = 0) => Ack(ResponseResult(result, target, operation, response, extraA, extraB));

    public static byte[] ResultAck(int result) => Build(writer => { writer.Write((byte)1); writer.Write(result); });

    public static byte[] GiftAck(int result, uint? goldRaw = null) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(result);
        if (result == 0) writer.Write(goldRaw ?? throw new ArgumentNullException(nameof(goldRaw)));
    });

    public static byte[] RingAck(int result, byte level) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(result); writer.Write(level);
    });

    public static byte[] LetterAck(int result, ushort? remaining = null) => Build(writer =>
    {
        writer.Write((byte)1); writer.Write(result);
        if (result == 0) writer.Write(remaining ?? throw new ArgumentNullException(nameof(remaining)));
    });

    public static byte[] CharacterAck(ReadOnlySpan<byte> rawCard)
    {
        if (rawCard.Length != 97) throw new ArgumentException("婚姻角色卡必须为97字节", nameof(rawCard));
        return Ack(rawCard);
    }

    public static byte[] Info(uint peerRaw, byte state, byte raw1, uint raw2, uint lovePoints, byte ringLevel) =>
        Build(writer =>
        {
            writer.Write(peerRaw); writer.Write(state); writer.Write(raw1);
            writer.Write(raw2); writer.Write(lovePoints); writer.Write(ringLevel);
        });

    public static byte[] Avatar(byte raw0, byte raw1, string firstName, string secondName,
        uint raw2, byte raw3, uint raw4) => Build(writer =>
    {
        writer.Write(raw0); writer.Write(raw1); WriteName(writer, firstName); WriteName(writer, secondName);
        writer.Write(raw2); writer.Write(raw3); writer.Write(raw4);
    });

    public static byte[] Ceremony(MarriageCeremonyWire info, ushort remaining) => Build(writer =>
    {
        WriteCeremony(writer, info); writer.Write(remaining);
    });

    public static byte[] Letter(MarriageCeremonyWire info, byte rawFlag, uint rawTail) => Build(writer =>
    {
        WriteCeremony(writer, info); writer.Write(rawFlag); writer.Write(rawTail);
    });

    public static byte[] CeremonyBegin(uint raw0, uint raw1, string firstName, string secondName) => Build(writer =>
    {
        writer.Write(raw0); writer.Write(raw1); WriteName(writer, firstName); WriteName(writer, secondName);
    });

    public static byte[] InvitedGuests(IReadOnlyList<uint> targets)
    {
        if (targets.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(targets));
        return Build(writer => { writer.Write((byte)targets.Count); foreach (uint target in targets) writer.Write(target); });
    }

    public static byte[] LovePoints(uint target, uint points) => Build(writer => { writer.Write(target); writer.Write(points); });
    public static byte[] Ring(byte level) => [level];
    public static byte[] Divorce(byte reason) => [reason];

    static void WriteRequest(BinaryWriter writer, int result, uint target, byte operation, byte extraA, byte extraB)
    {
        writer.Write(result); writer.Write(target); writer.Write(operation); writer.Write(extraA); writer.Write(extraB);
    }

    static void WriteCeremony(BinaryWriter writer, MarriageCeremonyWire info)
    {
        writer.Write(info.ContextRaw); writer.Write(info.CeremonyRaw); writer.Write(info.Timestamp);
        writer.Write(info.FourthRaw); writer.Write(info.Type);
        WriteName(writer, info.FirstName); WriteName(writer, info.SecondName);
    }

    static void WriteName(BinaryWriter writer, string name)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        if (bytes.Length >= 30) throw new ArgumentException("婚礼名称超过固定缓冲区", nameof(name));
        writer.Write(bytes); writer.Write(new byte[30 - bytes.Length]);
    }

    static byte[] Ack(ReadOnlySpan<byte> body)
    {
        byte[] result = new byte[body.Length + 1];
        result[0] = 1; body.CopyTo(result.AsSpan(1)); return result;
    }

    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer); return stream.ToArray();
    }
}
