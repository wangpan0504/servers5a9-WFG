using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Marriage;

namespace DfoServer.Network.Builders;

// 家具材料目录在装配时冻结，场景锁内只按已提交摆放快照求和，不读取PVF或数据库。
internal sealed class MarriageAppearance(MarriageCatalog catalog, ItemCatalog items)
{
    readonly ImmutableDictionary<int, uint> scores = catalog.Furniture.Keys.ToImmutableDictionary(id => id,
        id => checked((uint)items.Get(id).ShopPrice.Materials.Sum(row => (long)row.Count)));

    internal static uint DecorationScore(MarriageRecord record, IReadOnlyDictionary<int, uint> scores) =>
        record.State == 4 ? checked((uint)record.Furniture.Values.Sum(row => (long)scores[row.ItemId])) : 0;

    public byte[] Body(Character character)
    {
        var record = character.Marriage;
        bool active = record?.Active == true && record.IsMember(character.Id);
        return MarriageCouplePackets.MarriageAttributes(character.Id, active ? record!.Other(character.Id) : 0u,
            active ? record!.State : (byte)0, active ? record!.RingLevel : (byte)0,
            active ? DecorationScore(record!, scores) : 0);
    }
}
