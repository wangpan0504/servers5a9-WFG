using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class CurrencyPackets
{
    // 354–359 为六色晶体，360–365 为六种灵魂。
    static readonly int[] AccountItemIds = [3033, 3034, 3035, 3036, 3037, 3262, 10100115, 10100116, 10099773, 10099774, 10099775, 10158124];
    public static IEnumerable<short> VirtualSlots => new short[] { 0, 1, 2 }.Concat(Enumerable.Range(354, 12).Select(slot => (short)slot));
    public static bool WriteVirtual(BinaryWriter writer, InventorySnapshot inventory, short slot)
    {
        if (slot is not (>= 0 and <= 2 or >= 354 and <= 365)) return false;
        int itemId = slot <= 2 ? slot : AccountItemIds[slot - 354];
        var kind = slot <= 2 ? (CurrencyKind)slot : CurrencyKind.BlackCube + slot - 354;
        writer.Write(slot);
        writer.Write(itemId);
        writer.Write(checked((uint)CurrencyWallet.Get(inventory, kind)));
        writer.Write(new byte[ItemListProtocolWriter.EntrySize - 10]);
        return true;
    }
    // NOTI53: 023D2A30 读 u8 + 两个 i32，023D2CEA 再读第三个 i32。
    public static byte[] Cera(AccountWallet wallet)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(wallet.Get(CurrencyKind.Cera));
        writer.Write(wallet.Get(CurrencyKind.TokenCera));
        writer.Write(wallet.Get(CurrencyKind.HappyTokenCera));
        return stream.ToArray();
    }
    public static byte[] GoldLimit(InventorySnapshot inventory) => [CurrencyService.UpgradeStage(inventory)];
    // ACK980 公共成功字节之后，02367B78 固定读取 stage:u8 + result:i32。
    public static byte[] GoldLimitUpgraded(InventorySnapshot inventory) => [1, CurrencyService.UpgradeStage(inventory), 0, 0, 0, 0];
}
