using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

// 只在原账号 owner 内读写；一个实例结束即释放，不从旧数据库明细恢复。
internal sealed class DungeonTransientReceipts
{
    sealed class Directory
    {
        public Dictionary<(ushort Character, Guid Instance), DungeonTransientReceipts> Instances { get; } = [];
    }
    static readonly GameCacheSlot<Directory> slot = new();
    public Dictionary<Guid, FrozenDungeonGroundDeath> Loot { get; } = [];
    public Dictionary<uint, DungeonGroundObject> Grounds { get; } = [];
    public Dictionary<uint, DungeonTransientPickup> Pickups { get; } = [];
    public Dictionary<int, DungeonTransientFatigue> Fatigue { get; } = [];
    public Dictionary<int, DungeonTransientFatigue> HopeAreaFatigue { get; } = [];

    public static DungeonTransientReceipts? Find(AccountOwners.Lease owner, DungeonRunIdentity identity) =>
        owner.RuntimeCache.Get(slot)?.Instances.GetValueOrDefault((identity.CharacterId, identity.InstanceId));

    public static DungeonTransientReceipts Get(AccountOwners.Lease owner, DungeonRunIdentity identity)
    {
        if (identity.CharacterId == 0 || identity.InstanceId == Guid.Empty || identity.TaskAttempt <= 0)
            throw new InvalidDataException("临时回执需要实际副本实例身份");
        var directory = owner.RuntimeCache.Get(slot);
        if (directory is null) owner.RuntimeCache.Set(slot, directory = new());
        var key = (identity.CharacterId, identity.InstanceId);
        if (!directory.Instances.TryGetValue(key, out var value)) directory.Instances.Add(key, value = new());
        return value;
    }

    public static FrozenDungeonGroundDeath? FindLoot(AccountOwners.Lease owner, ushort characterId, Guid eventId) =>
        owner.RuntimeCache.Get(slot)?.Instances.Where(row => row.Key.Character == characterId)
            .Select(row => row.Value.Loot.GetValueOrDefault(eventId)).FirstOrDefault(value => value is not null);

    public static void Forget(AccountOwners.Lease owner, DungeonRunIdentity identity) =>
        owner.RuntimeCache.Get(slot)?.Instances.Remove((identity.CharacterId, identity.InstanceId));

    public void StoreLoot(FrozenDungeonGroundDeath value)
    {
        bool exists = Loot.TryGetValue(value.Death.EventId, out var previous);
        if (exists) VerifyLoot(previous!, value);
        foreach (var ground in value.Grounds)
        {
            if (Grounds.TryGetValue(ground.Reservation.GroundKey, out var stored) && stored != ground)
                throw new InvalidDataException("同实例地面编号的原冻结对象变化");
        }
        if (!exists) Loot.Add(value.Death.EventId, value);
        foreach (var ground in value.Grounds)
        {
            Grounds[ground.Reservation.GroundKey] = ground;
        }
    }

    public static void VerifyLoot(FrozenDungeonGroundDeath original, FrozenDungeonGroundDeath value)
    {
        if (original.Identity != value.Identity || original.CommittedIdentity != value.CommittedIdentity || original.Map != value.Map ||
            original.Death.EventId != value.Death.EventId || original.Death.MonsterKey != value.Death.MonsterKey ||
            original.Death.CombatSequence != value.Death.CombatSequence || original.ResourceIdentity != value.ResourceIdentity ||
            original.UsesCollectionLedger != value.UsesCollectionLedger || !original.Grounds.SequenceEqual(value.Grounds))
            throw new InvalidDataException("同事件的临时掉落来源、集合或完整核心变化");
    }
}

internal sealed class DungeonTransientFatigue(string rule, long beforeRevision, CharacterFatigueState before, CharacterFatigueState after)
{
    public string Rule { get; } = rule;
    public long BeforeRevision { get; } = beforeRevision;
    public long Revision => BeforeRevision + 1;
    public CharacterFatigueState Before { get; } = before;
    public CharacterFatigueState After { get; } = after;
    public bool Committed { get; set; }
}

// 不保留整份Character/Inventory快照；仅保存本次拾取的实际UID、核心、余额和提交版本。
internal sealed class DungeonTransientPickup(DungeonGroundPickupPreparation preparation, long revision,
    InventorySlot destination, long uid, ItemCore? core, CurrencyKind? currency, int amount, long balance, long walletRevision,
    EpicPiecePickup? epicPiece = null)
{
    public DungeonGroundPickupPreparation Preparation { get; } = preparation;
    public long Revision { get; } = revision;
    public bool Committed { get; set; }

    public void Verify(DungeonGroundPickupPreparation incoming)
    {
        if (Preparation.Identity != incoming.Identity || Preparation.Ground != incoming.Ground)
            throw new InvalidDataException("临时拾取的原始来源变化");
    }

    public DungeonPickupCommit Replay(Character current, DungeonGroundPickupPreparation incoming)
    {
        Verify(incoming);
        if (!Committed) throw new InvalidOperationException("拾取结果尚未确认，不能按成功回放");
        bool same = current.Revision == Revision;
        bool projection = epicPiece is { } piece
            ? same && current.Inventory.EpicPieces.Revision == piece.BookRevision && current.Inventory.EpicPieces.Get(piece.Index) == piece.Balance
            : currency is { } kind
            ? same && (!CurrencyWallet.IsAccount(kind) || current.Inventory.AccountWallet.Revision == walletRevision) &&
                CurrencyWallet.Get(current.Inventory, kind) == balance
            : same && current.Inventory.Find(destination) is { } item && item.Uid == uid && item.Core == core;
        return new(current, true, destination, uid, projection, currency, amount) { EpicPiece = epicPiece };
    }
}
