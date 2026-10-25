using DfoServer.Game.EpicProduction;

namespace DfoServer.Network.Builders;

internal static class EpicProductionPackets
{
    // CBA953/CBA95F读两个u32；第二值达到资源max charge point时才进入成品展示。
    public static byte[] StartedOrFinished(int weapon,int charge)
    {
        if (weapon<=0 || charge<0) throw new InvalidDataException("史诗制作结果无效");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write((byte)1);writer.Write(weapon);writer.Write(charge);return stream.ToArray();
    }
    // 00CBAC36只读取新武器u32，不附加进度或重置任何计数。
    public static byte[] Changed(int weapon)
    {
        if (weapon<=0) throw new InvalidDataException("史诗换型武器无效");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write((byte)1);writer.Write(weapon);return stream.ToArray();
    }
    // CBAE32/CBAE41/CBAE4B：绝对进度、周次数、结果演出字节；不是材料消耗数量。
    public static byte[] Processed(int charge,int weeklyUses,bool resultEffect)
    {
        if (charge<0 || weeklyUses is <0 or >255) throw new InvalidDataException("史诗加工结果无效");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write((byte)1);writer.Write(charge);writer.Write((byte)weeklyUses);writer.Write(resultEffect?(byte)1:(byte)0);
        return stream.ToArray();
    }
    // CBA7DD 的首个u32未参与状态投影；CBA803随后读取28字节。未知字节保持客户端构造器默认0。
    // 概率面板种子保持0；CMD1421固定选项由装备实例字节回放，不以此字段猜测随机品级调度。
    public static byte[] State(ushort characterId,EpicProductionState state)
    {
        state.Validate();
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write((uint)characterId);writer.Write(0u);writer.Write(state.WeaponId==0?-1:state.WeaponId);writer.Write(state.Charge);
        writer.Write((byte)0);writer.Write((byte)state.WeeklyUses);writer.Write((byte)0);writer.Write((byte)0);
        writer.Write(state.IntermediateMaterialRemainder);writer.Write(state.OrdinaryMaterialRemainder);writer.Write(state.AdditionalWeeklyUses);
        return stream.ToArray();
    }
    // CBB21D/CBB228/CBB231：产量、催化剂组别、该组剩余材料累计量。
    public static byte[] Crafted(int count,int group,int remainder)
    {
        if (count<0 || group is <0 or >2 || remainder<0) throw new InvalidDataException("催化剂制作结果无效");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write((byte)1);writer.Write(count);writer.Write((byte)group);writer.Write(remainder);return stream.ToArray();
    }
    public static byte[] Rejected(ushort error) => [0,(byte)error,(byte)(error>>8)];
}
