using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class AccountMaterialPackets
{
    // 563独立于Main槽位；mode0清整表，mode1逐条更新，零余额删除。
    public static IEnumerable<byte[]> Balances(AccountWallet wallet, AccountWallet? before = null)
    {
        int[] ids = before is null
            ? wallet.Materials.Where(row => row.Value.Balance != 0).Select(row => row.Key).ToArray()
            : wallet.Materials.Keys.Union(before.Materials.Keys)
                .Where(id => wallet.Material(id).Balance != before.Material(id).Balance).Order().ToArray();
        if (ids.Length == 0)
        {
            if (before is null) yield return [0, 0];
            yield break;
        }
        for (int offset = 0; offset < ids.Length; offset += byte.MaxValue)
        {
            int count = Math.Min(byte.MaxValue, ids.Length - offset);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)(before is null && offset == 0 ? 0 : 1));
            writer.Write((byte)count);
            for (int i = offset; i < offset + count; i++)
            {
                writer.Write(ids[i]);
                writer.Write(wallet.Material(ids[i]).Balance);
            }
            yield return stream.ToArray();
        }
    }

    // 1460/1461按模板查找二元组并按原序格式化；本端发送已获得/上限，原生标签含义仍待实机核对。
    public static byte[] Limits(AccountWallet wallet, ushort character,
        IReadOnlyDictionary<int, AccountMaterialDefinition> definitions, DateTimeOffset now, bool account)
    {
        int week = AccountMaterialService.RaidWeek(now);
        var rows = definitions.Where(row => (account ? row.Value.WeeklyAccountLimit : row.Value.WeeklyCharacterLimit) != 0)
            .OrderBy(row => row.Key).ToArray();
        if (rows.Length > ushort.MaxValue) throw new InvalidDataException("攻坚材料额度行数超界");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)rows.Length);
        foreach (var (id, definition) in rows)
        {
            writer.Write(id);
            var material = wallet.Material(id);
            writer.Write(account ? material.EarnedIn(week) : material.EarnedBy(character, week));
            writer.Write(account ? definition.WeeklyAccountLimit : definition.WeeklyCharacterLimit);
        }
        return stream.ToArray();
    }
}
