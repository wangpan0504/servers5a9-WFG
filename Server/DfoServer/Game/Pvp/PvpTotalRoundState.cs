using System.Collections.Immutable;

namespace DfoServer.Game.Pvp;

internal sealed record PvpTotalRoundChoice(ushort OwnerCharacter, byte Index);

// 同一网络成员拥有三名战斗角色；耗尽、选人和回合序号只属于这场原局。
internal sealed class PvpTotalRoundState(ImmutableArray<PvpArenaFighter> fighters)
{
    readonly byte[] selected = [byte.MaxValue, byte.MaxValue];
    readonly bool[,] defeated = new bool[2, 3];
    public long Revision { get; private set; }
    public DateTimeOffset Deadline { get; private set; }
    public bool Choosing { get; private set; }
    public ImmutableArray<PvpTotalRoundChoice> Choices => fighters.Select((fighter, at) =>
        new PvpTotalRoundChoice(fighter.CharacterId, selected[at])).ToImmutableArray();

    public void BeginSelection(DateTimeOffset now)
    {
        Choosing = true;
        Deadline = now + TimeSpan.FromSeconds(45);
        Revision++;
    }

    public bool Select(Guid member, byte index)
    {
        int side = Side(member);
        if (!Choosing || side < 0 || index > 2 || defeated[side, index] || selected[side] != byte.MaxValue) return false;
        selected[side] = index;
        Revision++;
        return true;
    }

    public bool Start(bool expired = false)
    {
        if (!Choosing) return false;
        for (int side = 0; side < 2; side++)
        {
            if (selected[side] != byte.MaxValue) continue;
            if (!expired) return false;
            selected[side] = Enumerable.Range(0, 3).Where(index => !defeated[side, index]).Select(index => (byte)index).First();
        }
        Choosing = false;
        Revision++;
        return true;
    }

    public void Defeat(Guid member)
    {
        int side = Side(member);
        if (Choosing || side < 0 || selected[side] > 2) throw new InvalidOperationException("总力败者没有本回合出战角色");
        defeated[side, selected[side]] = true;
        selected[side] = byte.MaxValue;
        Revision++;
    }

    public bool Exhausted(Guid member)
    {
        int side = Side(member);
        return side >= 0 && Enumerable.Range(0, 3).All(index => defeated[side, index]);
    }

    int Side(Guid member)
    {
        for (int side = 0; side < fighters.Length; side++) if (fighters[side].Membership == member) return side;
        return -1;
    }
}
