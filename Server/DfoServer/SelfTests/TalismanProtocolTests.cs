using System.Buffers.Binary;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TalismanProtocolTests
{
    public static void RunCore()
    {
        int start = Count;
        byte[] equip = [0xD5, 0, 0x46, 0, 0x83, 0x55, 0xA1, 0];
        var equipped = TalismanRequests.Equip(equip);
        That(equipped.TargetSlot == 213 && equipped.SourceSlot == 70 && equipped.ItemId == 10573187,
            "符文装载保留目标、源格与完整32位模板编号");
        That(TalismanRequests.Remove([0xD7, 0]) == 215, "符文卸载使用独立双字节请求");
        byte[] refine = new byte[21];
        refine.AsSpan(0, 13).Fill(0xA5);
        BinaryPrimitives.WriteInt32LittleEndian(refine.AsSpan(13), 140);
        BinaryPrimitives.WriteInt32LittleEndian(refine.AsSpan(17), 69);
        var refined = TalismanRequests.Refine(refine);
        That(refined.MaterialSlot == 140 && refined.TalismanSlot == 69,
            "重锻读取转换后的材料格与护石引用，不复用通用表单偏移");

        byte[] compound = [0xA5,0xA5,0xA5,0xA5,0xA5,0xA5,0xA5,0xA5,0xA5,0xA5,0xA5,0xA5,0xA5,
            1,0,3,0,0,0,2,0,0,0,0,0,1,0,69,0,0xFF,0xFF,0xFF,0xFF];
        var compounded = TalismanRequests.Compound(compound);
        That(compounded.Family == 1 && compounded.Mode == 0 && compounded.Rule == 2 &&
            compounded.SourceSlots.SequenceEqual(new short[] { 0, 1, 69 }),
            "合成双字节引用与两个单字节选择器不会按32位错位读取");
        foreach (int width in new[] { 0, 1, 7, 9, 20, 22, 32, 34 })
        {
            byte[] body = new byte[width];
            if (width != 8) Reject(() => TalismanRequests.Equip(body));
            if (width != 21) Reject(() => TalismanRequests.Refine(body));
            if (width != 33) Reject(() => TalismanRequests.Compound(body));
        }
        Reject(() => TalismanRequests.Remove([0]));
        Reject(() => TalismanRequests.Remove([0, 0, 0]));
        Reject(() => TalismanRequests.Equip(new byte[8]));
        Mutate(compound, 15, 0);
        Mutate(compound, 15, 6);
        Mutate(compound, 19, -1);
        Mutate(compound, 19, 7);
        var duplicate = (byte[])compound.Clone();
        duplicate[25] = 0;
        That(TalismanRequests.Compound(duplicate).SourceSlots.SequenceEqual(new short[] { 0, 0, 69 }),
            "合成保留同格多次引用，供业务校验堆叠数量而非解析器去重");
        var tail = (byte[])compound.Clone();
        tail[29] = 0;
        Reject(() => TalismanRequests.Compound(tail));
        var negative = (byte[])refine.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negative.AsSpan(13), -1);
        Reject(() => TalismanRequests.Refine(negative));
        BinaryPrimitives.WriteInt32LittleEndian(negative.AsSpan(13), short.MaxValue + 1);
        Reject(() => TalismanRequests.Refine(negative));
        Console.WriteLine($"PASS {Count - start} talisman request checks");
    }

    static void Mutate(byte[] original, int offset, int value)
    {
        byte[] body = (byte[])original.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), value);
        Reject(() => TalismanRequests.Compound(body));
    }

    static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException) { rejected = true; }
        That(rejected, "畸形护石请求不得进入库存业务");
    }
}
