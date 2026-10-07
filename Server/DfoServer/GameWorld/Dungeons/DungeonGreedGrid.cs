namespace DfoServer.GameWorld.Dungeons;

// SourceOrdinal只表示文本消费次序，TextOffset为UTF16字符偏移；不提供坐标方向或出口位。
internal sealed record DungeonGreedCell(int SourceOrdinal, int TextOffset, string Code, byte Index);

internal sealed record DungeonGreedGrid(int Width, int Height, string SourceText,
    IReadOnlyList<DungeonGreedCell>? Cells, string? UnsupportedReason)
{
    public bool Supported => Cells is not null;
    public string Format => Supported ? "ascii-pairs" : "unsupported";

    public static DungeonGreedGrid Read(int width, int height, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        long expectedCount = (long)width * height;
        if (width <= 0 || height <= 0 || expectedCount > int.MaxValue)
            return Unsupported("greed 尺寸不是支持范围内的正整数网格");
        var cells = new List<DungeonGreedCell>();
        for (int offset = 0; offset < text.Length;)
        {
            // 0409C71F只在当前位置识别这三种字符，不先删除所有空白或按换行分配坐标。
            char first = text[offset];
            if (first is '\r' or '\n' or ' ')
            {
                offset++;
                continue;
            }

            if (offset + 1 >= text.Length)
                return Unsupported("greed 在UTF16偏移 " + offset + " 缺少第二个编码字符");
            // 当前冻结flag=0的表为AA..PP；另一Unicode表的选择与消费合同尚未闭合。
            if (first is < 'A' or > 'P' || text[offset + 1] != first)
                return Unsupported("greed 在UTF16偏移 " + offset + " 不是已确认的AA..PP编码");
            cells.Add(new(cells.Count, offset, text.Substring(offset, 2), checked((byte)(first - 'A'))));
            if (cells.Count > expectedCount)
                return Unsupported("greed 编码格数超过width×height");
            offset += 2;
        }

        if (cells.Count != expectedCount)
            return Unsupported("greed 编码格数与width×height不一致");
        return new(width, height, text, Array.AsReadOnly(cells.ToArray()), null);

        // 不发布部分格，也不以AA/0补齐未知输入；原文和尺寸仍完整保留供取证。
        DungeonGreedGrid Unsupported(string reason) => new(width, height, text, null, reason);
    }
}
