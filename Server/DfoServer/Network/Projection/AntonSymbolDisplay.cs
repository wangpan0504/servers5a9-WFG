namespace DfoServer.Network.Projection;

internal readonly record struct AntonSymbolDisplayKey(Guid Instance, int Phase, long SelectionEpoch, long SceneEpoch);

// 每连接只保留当前实例/场景的符号；由发送泵在本人owner内读取并在写帧成功后确认。
internal sealed class AntonSymbolDisplay
{
    internal AntonSymbolDisplayKey? Key { get; private set; }
    internal int? Infection { get; private set; }
    readonly Dictionary<int, int> values = [];

    internal bool Changed(AntonSymbolDisplayKey key, int id, int value) =>
        Key != key || !values.TryGetValue(id, out int previous) || previous != value;

    void Reset(AntonSymbolDisplayKey key)
    {
        if (Key == key) return;
        Key = key;
        values.Clear();
        Infection = null;
    }

    internal void Record(AntonSymbolDisplayKey key, IEnumerable<(int Id, int Value)> rows)
    {
        Reset(key);
        foreach (var row in rows) values[row.Id] = row.Value;
    }

    internal void RecordInfection(AntonSymbolDisplayKey key, int infection)
    {
        Reset(key);
        Infection = infection;
        // 585的非零尾身份会将客户端symbol7置1，随后583必须恢复真实值。
        if (infection != 0) values.Remove(7);
    }
}
