using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Trade;

internal readonly record struct TradeParticipant(ushort CharacterId, long Generation, long SelectionEpoch);
internal readonly record struct TradeLine(ushort Slot, InventoryItem Item, int Count);
internal sealed record TradeOffer(uint Gold, ImmutableArray<TradeLine> Items);
internal readonly record struct TradeView(Guid Id, TradeParticipant First, TradeParticipant Second, bool Accepted, Func<bool> IsCurrent)
{
    public TradeParticipant Other(TradeParticipant participant) => participant == First ? Second : First;
}

internal sealed class TradeTicket(TradeParticipant first, TradeParticipant second, TradeOffer firstOffer, TradeOffer secondOffer)
{
    int status;
    public TradeParticipant First { get; } = first;
    public TradeParticipant Second { get; } = second;
    public TradeOffer FirstOffer { get; } = firstOffer;
    public TradeOffer SecondOffer { get; } = secondOffer;
    public bool TryBeginCommit() => Interlocked.CompareExchange(ref status, 1, 0) == 0;
    internal bool TryCancel() => Interlocked.CompareExchange(ref status, -1, 0) == 0;
    public bool IsCancelled => Volatile.Read(ref status) == -1;
}

internal sealed class TradeRegistry
{
    sealed class Entry(TradeParticipant first, TradeParticipant second, uint token, long expires)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TradeParticipant First { get; } = first;
        public TradeParticipant Second { get; } = second;
        public uint Token { get; } = token;
        public long Expires { get; } = expires;
        public bool Accepted;
        public readonly bool[] Locked = new bool[2];
        public readonly bool[] Confirmed = new bool[2];
        public readonly uint[] Gold = new uint[2];
        public readonly List<TradeLine>[] Items = [[], []];
        public TradeTicket? Ticket;
        int active = 1;
        public bool IsCurrent() => Volatile.Read(ref active) == 1;
        public void Invalidate() => Interlocked.Exchange(ref active, 0);
        public int Index(TradeParticipant participant) => participant == First ? 0 : participant == Second ? 1 : -1;
        public TradeTicket Snapshot() => new(First, Second, new(Gold[0], [.. Items[0]]), new(Gold[1], [.. Items[1]]));
    }

    readonly object gate = new();
    readonly Dictionary<ushort, Entry> entries = [];

    public TradeView? Find(TradeParticipant participant)
    {
        lock (gate)
            return Find(participant, out var entry, out _)
                ? new TradeView(entry.Id, entry.First, entry.Second, entry.Accepted, entry.IsCurrent) : null;
    }

    public bool Invite(TradeParticipant first, TradeParticipant second, uint token, long now, out Guid tradeId)
    {
        tradeId = Guid.Empty;
        lock (gate)
        {
            Expire(first.CharacterId, now);
            Expire(second.CharacterId, now);
            if (first.CharacterId == 0 || second.CharacterId == 0 || first.CharacterId == second.CharacterId ||
                entries.ContainsKey(first.CharacterId) || entries.ContainsKey(second.CharacterId)) return false;
            var entry = new Entry(first, second, token, checked(now + 30));
            entries.Add(first.CharacterId, entry);
            entries.Add(second.CharacterId, entry);
            tradeId = entry.Id;
            return true;
        }
    }

    public bool Accept(TradeParticipant second, TradeParticipant first, long now, out uint token, out Guid tradeId)
    {
        token = 0;
        tradeId = Guid.Empty;
        lock (gate)
        {
            if (!entries.TryGetValue(second.CharacterId, out var entry) || entry.First != first || entry.Second != second || entry.Accepted)
                return false;
            if (now > entry.Expires)
            {
                Remove(entry);
                return false;
            }
            entry.Accepted = true;
            token = entry.Token;
            tradeId = entry.Id;
            return true;
        }
    }

    public bool Put(TradeParticipant participant, InventoryItem item, int count, out ushort slot, Guid? tradeId = null)
    {
        slot = 0;
        lock (gate)
        {
            if (!Editable(participant, tradeId, out var entry, out int index) || item.Uid <= 0 || count <= 0 || count > item.Core.Count)
                return false;
            var lines = entry.Items[index];
            int existing = lines.FindIndex(line => line.Item.Uid == item.Uid);
            if (existing >= 0)
            {
                slot = lines[existing].Slot;
                lines[existing] = new(slot, item, count);
                return true;
            }
            for (ushort candidate = 3; candidate <= 11; candidate++)
            {
                if (lines.Any(line => line.Slot == candidate)) continue;
                slot = candidate;
                lines.Add(new(slot, item, count));
                return true;
            }
            return false;
        }
    }

    public bool Take(TradeParticipant participant, ushort slot, out TradeLine line, Guid? tradeId = null,
        InventorySlot? destination = null, uint? itemId = null, uint? count = null, InventoryItem? currentItem = null)
    {
        line = default;
        lock (gate)
        {
            if (!Editable(participant, tradeId, out var entry, out int index)) return false;
            int position = entry.Items[index].FindIndex(item => item.Slot == slot);
            if (position < 0) return false;
            line = entry.Items[index][position];
            if (destination is { } expected && line.Item.Position != expected ||
                itemId is { } expectedItem && line.Item.Core.ItemId != expectedItem ||
                count is { } expectedCount && line.Count != expectedCount ||
                currentItem is not null && line.Item != currentItem) return false;
            entry.Items[index].RemoveAt(position);
            return true;
        }
    }

    public bool ChangeGold(TradeParticipant participant, uint amount, bool take, out uint gold, Guid? tradeId = null)
    {
        gold = 0;
        lock (gate)
        {
            if (!Editable(participant, tradeId, out var entry, out int index)) return false;
            if (take)
            {
                if (amount > entry.Gold[index]) return false;
                entry.Gold[index] -= amount;
            }
            else
            {
                if ((ulong)entry.Gold[index] + amount > uint.MaxValue) return false;
                entry.Gold[index] += amount;
            }
            gold = entry.Gold[index];
            return true;
        }
    }

    public bool Lock(TradeParticipant participant, out bool both, Guid? tradeId = null)
    {
        both = false;
        lock (gate)
        {
            if (!Editable(participant, tradeId, out var entry, out int index)) return false;
            entry.Locked[index] = true;
            both = entry.Locked[0] && entry.Locked[1];
            return true;
        }
    }

    public bool Confirm(TradeParticipant participant, out TradeTicket? ticket, Guid? tradeId = null)
    {
        ticket = null;
        lock (gate)
        {
            if (!Find(participant, out var entry, out int index) || tradeId is { } expected && entry.Id != expected || !entry.Accepted || entry.Ticket is not null ||
                !entry.Locked[0] || !entry.Locked[1] || entry.Confirmed[index]) return false;
            entry.Confirmed[index] = true;
            if (entry.Confirmed[0] && entry.Confirmed[1]) ticket = entry.Ticket = entry.Snapshot();
            return true;
        }
    }

    public TradeTicket? Cancel(TradeParticipant participant, Guid tradeId)
    {
        lock (gate)
        {
            if (!Find(participant, out var entry, out _) || entry.Id != tradeId) return null;
            var ticket = entry.Ticket ?? entry.Snapshot();
            if (!ticket.TryCancel()) return null;
            Remove(entry);
            return ticket;
        }
    }

    public void Complete(TradeTicket ticket)
    {
        lock (gate)
        {
            if (entries.TryGetValue(ticket.First.CharacterId, out var entry) && ReferenceEquals(entry.Ticket, ticket))
                Remove(entry);
        }
    }

    public bool Cancel(TradeTicket ticket)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(ticket.First.CharacterId, out var entry) || !ReferenceEquals(entry.Ticket, ticket) || !ticket.TryCancel())
                return false;
            Remove(entry);
            return true;
        }
    }

    bool Find(TradeParticipant participant, out Entry entry, out int index)
    {
        if (entries.TryGetValue(participant.CharacterId, out var found) && (index = found.Index(participant)) >= 0)
        {
            entry = found;
            return true;
        }
        entry = null!;
        index = -1;
        return false;
    }

    bool Editable(TradeParticipant participant, Guid? tradeId, out Entry entry, out int index) =>
        Find(participant, out entry, out index) && (tradeId is null || entry.Id == tradeId) && entry.Accepted && entry.Ticket is null && !entry.Locked[index];

    void Remove(Entry entry)
    {
        entry.Invalidate();
        entries.Remove(entry.First.CharacterId);
        entries.Remove(entry.Second.CharacterId);
    }

    void Expire(ushort characterId, long now)
    {
        if (entries.TryGetValue(characterId, out var entry) && !entry.Accepted && now > entry.Expires)
            Remove(entry);
    }
}
