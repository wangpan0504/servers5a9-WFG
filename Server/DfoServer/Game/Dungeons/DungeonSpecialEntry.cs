namespace DfoServer.Game.Dungeons;

internal enum DungeonSpecialEntryKind { Blood = 1, Ultimate = 2, Despair = 3 }
internal sealed record DungeonSpecialEntryUsage(int CharacterEntries, int AccountCharacters);

// 入场事实和钱款与原尝试号共事务；此合同只记录已批准的入场，不从计数推测未证的每日上限。
internal sealed record DungeonSpecialEntry(int Day, DungeonSpecialEntryKind Kind, int Layer, int Gold,
    DungeonSpecialEntryUsage Expected)
{
    public DungeonDespairProgress? DespairProgress { get; init; }
    public void Validate()
    {
        if (Day is < 1 or > 3652058 || !Enum.IsDefined(Kind) || Gold < 0 ||
            (Kind == DungeonSpecialEntryKind.Despair ? Layer is < 1 or > 100 : Layer != 0) ||
            Expected.CharacterEntries < 0 || Expected.AccountCharacters < 0)
            throw new InvalidDataException("塔或祭坛入场事实无效");
        if (Kind == DungeonSpecialEntryKind.Despair)
        {
            if (DespairProgress is not { } progress || progress.NextLayer != Layer || progress.LastClearDay == Day || Gold != 0)
                throw new InvalidDataException("绝望入场缺少原楼层或混入祭坛金币费用");
            progress.Validate();
        }
        else if (DespairProgress is not null) throw new InvalidDataException("祭坛入场不能携带绝望进度");
    }
}
