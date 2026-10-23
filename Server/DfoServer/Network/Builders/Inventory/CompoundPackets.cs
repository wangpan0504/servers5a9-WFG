using System.Buffers.Binary;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Network.Builders.Inventory;

internal static class CompoundPackets
{
    // ARAD 0236C6D0：消耗行之后是结果标记与产物行数，不是u16奖励数或尾部填充。
    public static byte[] Result(InventoryResult result, RecipeCraftReceipt? recipe = null)
    {
        if (!result.Accepted)
        {
            byte[] failure = new byte[3];
            BinaryPrimitives.WriteUInt16LittleEndian(failure.AsSpan(1), result.Error);
            return failure;
        }
        if (recipe is null || result.Receipt is not { } committed)
            throw new InvalidDataException("制作成功回复缺少已提交实例与配方回执");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(checked((byte)recipe.Consumed.Length));
        foreach (var consumed in recipe.Consumed)
        {
            if (consumed.Position.List != InventoryListType.Main || consumed.Count <= 0)
                throw new InvalidDataException("制作消耗必须使用已确认的Main槽位");
            writer.Write((byte)0);
            writer.Write(consumed.Position.Index);
            writer.Write(consumed.Count);
        }
        writer.Write(recipe.Success ? (byte)1 : (byte)2);
        writer.Write(checked((byte)recipe.Products.Length));
        foreach (var product in recipe.Products)
        {
            writer.Write((byte)0);
            if (CurrencyWallet.RewardKind(product.ItemId) is { } currency)
            {
                if (CurrencyWallet.Get(committed.Character.Inventory, currency) < product.Count ||
                    !CurrencyPackets.WriteVirtual(writer, CurrencyWallet.Set(committed.Character.Inventory, currency, product.Count), product.Position.Index))
                    throw new InvalidDataException("制作钱包产物与提交结果不符");
                continue;
            }
            var item = committed.Index.Find(product.Position);
            if (item is not { Uid: > 0 } || item.Core.ItemId != product.ItemId || product.Count <= 0 || product.Count > item.Core.Count)
                throw new InvalidDataException("制作产物与实际提交实例不符");
            // 原生消费者对已有堆叠执行增加，填本次获得量，属性仍取已提交的完整核心。
            ItemListProtocolWriter.Write(writer, product.Position.Index, item.Core.IsStackable ? item.Core with { Value = product.Count } : item.Core);
        }
        return stream.ToArray();
    }

    public static byte[] MakingSkills(ItemMakingSkillState state)
    {
        state.Validate();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)ItemMakingSkillState.Count);
        for (byte type = 0; type < ItemMakingSkillState.Count; type++)
        { writer.Write(type); writer.Write(state.Points[type]); }
        return stream.ToArray();
    }

    public static byte[] MakingGain(ItemMakingSkillGain gain) => [gain.Status, gain.Type, (byte)gain.Amount, (byte)(gain.Amount >> 8)];
}
