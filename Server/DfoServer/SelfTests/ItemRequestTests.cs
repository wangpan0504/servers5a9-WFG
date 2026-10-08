using static DfoServer.Testing.TestCheck;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class ItemRequestTests
{
    public static void Run()
    {
        int checks = Count;
        // 固定字节样本独立给出；它们是宽度/字序 fixture，不是客户端实收请求。
        byte[] moveBody = [0x03, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12, 0xD4, 0xC3, 0xB2, 0xA1,
            0x00, 0x78, 0x56, 0x40, 0x30, 0x20, 0x10, 0x66, 0x77, 0x88, 0x99,
            0xFF, 0xFF, 0xFF, 0xFF, 0x5A, 0xC3];
        var move = ItemRequests.ParseMove(moveBody);
        That(move.First == new ItemMoveEndpoint(3, 0x1234, 0x12345678, 0xA1B2C3D4), "移动第一字段组及未知值");
        That(move.Second == new ItemMoveEndpoint(0, 0x5678, 0x10203040, 0x99887766), "移动第二字段组及未知值");
        That(move.ContextIndex == -1 && move.Tail0 == 0x5A && move.Tail1 == 0xC3, "移动末尾六字节保留");
        moveBody[0] = 0xFF;
        That(move.First.Space == 3, "解析结果不引用可变输入");

        byte[] buyBody = [0x67, 0x45, 0x23, 0x01, 0xEF, 0xCD, 0xAB, 0x89,
            0xA9, 0xCB, 0xED, 0x1F, 0x10, 0x32, 0x54, 0x76];
        var buy = ItemRequests.ParseBuy(buyBody);
        That(buy == new BuyItemRequest(0x01234567, 0x89ABCDEF, 0x1FEDCBA9, 0x76543210), "购买四个原始DWORD");

        byte[] sellBody = [0x02, 0x34, 0x12, 0x02, 0x01, 0x00, 0x00,
            0x44, 0x33, 0x22, 0x11, 0x88, 0x77, 0x66, 0x55, 0xCC, 0xBB, 0xAA, 0x99];
        var sell = ItemRequests.ParseSell(sellBody);
        That(sell == new SellItemRequest(2, 0x1234, 0x102, 0x11223344, 0x55667788, 0x99AABBCC), "出售字段宽度及字序");

        byte[] moveMaximum = Enumerable.Repeat((byte)0xFF, 28).ToArray();
        var moveUnsignedMaximum = ItemRequests.ParseMove(moveMaximum);
        That(moveUnsignedMaximum.First == new ItemMoveEndpoint(byte.MaxValue, ushort.MaxValue, uint.MaxValue, uint.MaxValue), "第一组最大值无截断");
        That(moveUnsignedMaximum.Second == moveUnsignedMaximum.First, "第二组最大值无截断");
        That(moveUnsignedMaximum.ContextIndex == -1 && moveUnsignedMaximum.Tail0 == byte.MaxValue && moveUnsignedMaximum.Tail1 == byte.MaxValue, "末字段最大原始位模式");
        moveMaximum[25] = 0x7F;
        That(ItemRequests.ParseMove(moveMaximum).ContextIndex == int.MaxValue, "有符号上下文最大值");
        moveMaximum[22] = 0;
        moveMaximum[23] = 0;
        moveMaximum[24] = 0;
        moveMaximum[25] = 0x80;
        That(ItemRequests.ParseMove(moveMaximum).ContextIndex == int.MinValue, "有符号上下文最小值");
        That(ItemRequests.ParseBuy(Enumerable.Repeat((byte)0xFF, 16).ToArray()) == new BuyItemRequest(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue), "购买DWORD最大值");
        That(ItemRequests.ParseSell(Enumerable.Repeat((byte)0xFF, 19).ToArray()) == new SellItemRequest(byte.MaxValue, ushort.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue), "出售最大值无截断");

        for (int length = 0; length < 28; length++)
        {
            Reject(() => ItemRequests.ParseMove(new byte[length]), "移动短包 " + length);
        }
        Reject(() => ItemRequests.ParseMove(new byte[29]), "移动长包");
        for (int length = 0; length < 16; length++)
        {
            Reject(() => ItemRequests.ParseBuy(new byte[length]), "购买短包 " + length);
        }
        Reject(() => ItemRequests.ParseBuy(new byte[17]), "购买长包");
        for (int length = 0; length < 19; length++)
        {
            Reject(() => ItemRequests.ParseSell(new byte[length]), "出售短包 " + length);
        }
        Reject(() => ItemRequests.ParseSell(new byte[20]), "出售长包");
        That(ItemRequests.ParseMove(new byte[28]).First.Value == 0, "移动零值留给业务校验");
        That(ItemRequests.ParseBuy(new byte[16]).QuantityValue == 0, "购买零值留给业务校验");
        That(ItemRequests.ParseSell(new byte[19]).Quantity == 0, "出售零值留给业务校验");
        Console.WriteLine($"PASS {Count - checks} item request assertions");

        void Reject(Action action, string name)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            That(rejected, name);
        }
    }
}
