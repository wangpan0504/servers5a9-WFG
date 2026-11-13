using System.Collections.Concurrent;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.ExpertJob;

internal sealed record ExpertJobStoreRecord(ushort OwnerId, byte JobType, byte Mode, string Name,
    uint First, ushort Second, ushort Third, ushort Fourth, byte Grade, uint Endurance)
{
    // 开店时的可信场景身份，不能把请求中的服务费当作场景编号。
    public byte Town { get; init; }
    public byte Area { get; init; }
    public ImmutableArray<byte> Rarities { get; init; } = [];

    // 店铺独立保存有效资格快照，供开店、后入场和进入店铺投影共用。
    public ExpertJobStoreRecord WithStatus(Character character, ExpertJobScriptCatalog scripts)
    {
        if (character.Id != OwnerId || character.ExpertJobType != JobType)
            throw new InvalidOperationException("店铺状态不属于当前摊主");
        byte grade = checked((byte)(JobType == 3 ? character.ExpertMachineGrade : scripts.Level(JobType, character.ExpertJobExperience)));
        byte[] rarities = JobType == 1 ? scripts.LearnedRarities(scripts.LearnedRecipes(JobType, grade, character.ExpertJobRecipes)) : [];
        return this with
        {
            Grade = grade, Name = ((char)grade).ToString(), Endurance = (uint)Math.Max(0, character.ExpertMachineEndurance),
            Rarities = rarities.SequenceEqual(Rarities) ? Rarities : [.. rarities]
        };
    }

    public Guid Instance { get; init; } = Guid.NewGuid();
}

internal sealed record PrivateStoreOffer(byte StallSlot, byte SourceList, short SourceSlot, int ItemId, int Count, uint UnitPrice);

internal sealed record PrivateStoreRecord(ushort OwnerId, string Title, byte Town, byte Area,
    ImmutableArray<PrivateStoreOffer> Offers)
{
    public const int MaxOffers = 12;
    public Guid Instance { get; init; } = Guid.NewGuid();

    public bool TryPurchase(byte stallSlot, int count, uint expectedUnitPrice, out PrivateStoreOffer purchased,
        out PrivateStoreRecord updated, out uint totalPrice)
    {
        purchased = default!;
        updated = this;
        totalPrice = 0;
        var offer = Offers.FirstOrDefault(row => row.StallSlot == stallSlot);
        if (offer is null || count <= 0 || count > offer.Count || offer.UnitPrice == 0 || offer.UnitPrice != expectedUnitPrice)
            return false;
        long total = (long)offer.UnitPrice * count;
        if (total <= 0 || total > uint.MaxValue)
            return false;
        totalPrice = (uint)total;
        purchased = offer with { Count = count };
        var nextOffers = count == offer.Count
            ? Offers.Remove(offer)
            : Offers.Replace(offer, offer with { Count = offer.Count - count });
        updated = this with { Offers = nextOffers };
        return true;
    }
}

internal sealed class ExpertJobStoreBoard
{
    readonly ConcurrentDictionary<ushort, ExpertJobStoreRecord> stores = new();
    readonly ConcurrentDictionary<ushort, PrivateStoreRecord> privateStores = new();

    public void Open(ExpertJobStoreRecord store) => stores[store.OwnerId] = store;

    public bool TryUpdate(ExpertJobStoreRecord before, ExpertJobStoreRecord current) =>
        before.OwnerId == current.OwnerId && before.Instance == current.Instance && stores.TryUpdate(before.OwnerId, current, before);

    public bool TryClose(ushort owner, out ExpertJobStoreRecord store) => stores.TryRemove(owner, out store!);

    public bool TryGet(ushort owner, out ExpertJobStoreRecord store) => stores.TryGetValue(owner, out store!);

    public void OpenPrivateStore(PrivateStoreRecord store)
    {
        if (store.Offers.IsDefaultOrEmpty || store.Offers.Length > PrivateStoreRecord.MaxOffers)
            throw new InvalidDataException("个人摆摊上架栏位数量非法");
        privateStores[store.OwnerId] = store;
    }

    public bool TryGetPrivateStore(ushort owner, out PrivateStoreRecord store) => privateStores.TryGetValue(owner, out store!);

    public bool TryUpdatePrivateStore(PrivateStoreRecord before, PrivateStoreRecord current) =>
        before.OwnerId == current.OwnerId && before.Instance == current.Instance && privateStores.TryUpdate(before.OwnerId, current, before);

    public bool TryClosePrivateStore(ushort owner, out PrivateStoreRecord store) => privateStores.TryRemove(owner, out store!);
}
