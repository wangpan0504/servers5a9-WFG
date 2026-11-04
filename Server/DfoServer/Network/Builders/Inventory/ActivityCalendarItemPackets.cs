using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

// 原生日历读取44/45，商店按材料声明选择其一；仅投影，不创建持久实例。
internal static class ActivityCalendarItemPackets
{
    public const byte RaidList = 44, CommonList = 45;

    public static byte[] Full(byte list, AccountWallet wallet, IReadOnlyList<int> itemIds) =>
        Build(list, wallet, itemIds, Enumerable.Range(0, itemIds.Count).Where(slot => wallet.Material(itemIds[slot]).Balance > 0).ToArray());

    // 正向变化使用14播报真实增量，减少使用1681静默更新；行内始终是绝对余额。
    public static IEnumerable<(byte[] Body, bool Obtained)> Changes(byte list, AccountWallet before, AccountWallet wallet,
        IReadOnlyList<int> itemIds)
    {
        foreach (bool obtained in new[] { false, true })
        {
            int[] slots = Enumerable.Range(0, itemIds.Count).Where(slot =>
                wallet.Material(itemIds[slot]).Balance != before.Material(itemIds[slot]).Balance &&
                (wallet.Material(itemIds[slot]).Balance > before.Material(itemIds[slot]).Balance) == obtained).ToArray();
            if (slots.Length != 0) yield return (Build(list, wallet, itemIds, slots), obtained);
        }
    }

    static byte[] Build(byte list, AccountWallet wallet, IReadOnlyList<int> itemIds, int[] slots)
    {
        int capacity = list switch { RaidList => 64, CommonList => 50, _ => throw new InvalidDataException("活动账号材料容器无效") };
        if (itemIds.Count > capacity || itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count)
            throw new InvalidDataException("活动账号材料超出原生容器容量或模板登记无效");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(list);
        writer.Write(checked((ushort)slots.Length));
        foreach (int slot in slots)
        {
            int id = itemIds[slot], count = wallet.Material(id).Balance;
            ItemListProtocolWriter.Write(writer, checked((short)slot), count > 0 ? new ItemCore(id, ItemKind.Material, count) : null);
        }
        return stream.ToArray();
    }
}
