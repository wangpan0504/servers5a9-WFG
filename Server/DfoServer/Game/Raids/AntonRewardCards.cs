namespace DfoServer.Game.Raids;

// 归Battle所有，调用方持Battle锁；最多两阶段×20成员，随实例回收。
// 95控件01C74425..01C74B7E→022A165A证明界面第1–4张对应wire 0–3。
internal sealed class AntonRewardCards
{
    readonly Dictionary<(int Phase, ushort Character), (byte Squad, byte Card, byte Requested)> selected = [];

    internal bool MatchesReplay(int phase, ushort character, byte requested, byte assigned) =>
        selected.TryGetValue((phase, character), out var previous) &&
        previous.Card == assigned && previous.Requested == requested;

    internal byte? Select(int phase, ushort character, byte squad, byte requested)
    {
        if (phase is < 0 or > 1 || squad is < 1 or > 5 || requested > 3) return null;
        if (selected.TryGetValue((phase, character), out var previous)) return previous.Card;
        // 86 Rewards.cs:95–130：同队冲突时取首个空位，两种奖励共享该成员选位。
        var used = selected.Where(row => row.Key.Phase == phase && row.Value.Squad == squad)
            .Select(row => row.Value.Card).ToArray();
        byte card = requested;
        if (used.Contains(card))
        {
            var free = Enumerable.Range(0, 4).FirstOrDefault(slot => !used.Contains((byte)slot), -1);
            if (free < 0) return null;
            card = (byte)free;
        }
        selected.Add((phase, character), (squad, card, requested));
        return card;
    }
}
