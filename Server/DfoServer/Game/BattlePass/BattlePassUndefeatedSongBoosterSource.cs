using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.BattlePass;

internal sealed record BattlePassSongBoosterFact(string SourceId, int Amount, DateTimeOffset At);

internal static class BattlePassUndefeatedSongBoosterSource
{
    public const string SourceKind = "box_undefeated_song_acquisition";

    public static BattlePassSongBoosterFact? Prepare(ushort character, long revision, InventoryPlan plan)
    {
        if (plan.BoosterSongAcquisition is not { } fact) return null;
        if (character == 0 || revision < 0 || fact.Character != character || fact.Revision != revision ||
            plan.Reason is not ("booster_open" or "lottery_open") || plan.CharacterPrecondition is null ||
            fact.Source.Uid <= 0 || fact.Source.Position.List != InventoryListType.Main ||
            fact.Source.Core.ItemId <= 0 || fact.Source.Core.ItemId == BoosterSongAcquisition.ItemId ||
            fact.OpenCount <= 0 || fact.OpenCount > fact.Source.Core.Count || fact.DeclaredAmount <= 0 ||
            plan.BeforeIndex.Find(fact.Source.Position) != fact.Source ||
            !plan.UsedItems.SequenceEqual([new ItemUseIntent(fact.Source.Core.ItemId, fact.OpenCount)]))
            throw new InvalidDataException("不渝之咏原开箱取得身份或扣源不一致");
        var remaining = plan.After.Items.SingleOrDefault(item => item.Uid == fact.Source.Uid);
        int left = fact.Source.Core.Count - fact.OpenCount;
        if (left == 0 ? remaining is not null : remaining is null || remaining.Position != fact.Source.Position ||
            remaining.Core != (fact.Source.Core with { Value = left }))
            throw new InvalidDataException("不渝之咏原箱UID或完整核心未按原次数消费");
        long overflow = 0;
        foreach (var item in plan.OverflowItems.Where(item => item.Core.ItemId == BoosterSongAcquisition.ItemId))
        {
            if (item.Core.Kind != ItemKind.Material || item.Core.Count <= 0)
                throw new InvalidDataException("不渝之咏原开箱投邮核心无效");
            overflow = checked(overflow + item.Core.Count);
        }
        long physical = checked(MainAmount(plan.After) - MainAmount(plan.Before));
        long account = (long)plan.After.AccountWallet.Material(BoosterSongAcquisition.ItemId).Balance -
            plan.Before.AccountWallet.Material(BoosterSongAcquisition.ItemId).Balance;
        if (physical < 0 || account < 0)
            throw new InvalidDataException("不渝之咏原奖池数量与实际取得不一致");
        long acquired = checked(physical + account);
        if (acquired > int.MaxValue || overflow > fact.DeclaredAmount ||
            acquired != fact.DeclaredAmount - overflow)
            throw new InvalidDataException("不渝之咏原奖池数量与实际入包和投邮不一致");
        if (acquired == 0) return null;
        string identity = FormattableString.Invariant($"{character}:{revision}:{fact.Source.Uid}:{fact.Source.Core.ItemId}:{fact.OpenCount}:{fact.DeclaredAmount}");
        string source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceKind + ":" + identity)).AsSpan(0, 16)).ToLowerInvariant();
        return new(source, checked((int)acquired), fact.At);
    }

    static long MainAmount(InventorySnapshot inventory)
    {
        long amount = 0;
        foreach (var item in inventory.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == BoosterSongAcquisition.ItemId))
        {
            if (item.Core.Kind != ItemKind.Material || item.Core.Count <= 0)
                throw new InvalidDataException("不渝之咏原开箱实物核心无效");
            amount = checked(amount + item.Core.Count);
        }
        return amount;
    }
}
