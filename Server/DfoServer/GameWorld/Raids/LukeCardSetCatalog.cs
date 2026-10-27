using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

// UI只读声明目录；Ready仅表示声明/引用可读，不授予路线选择、概率或symbol写入能力。
internal sealed record LukeCardSetCatalog(PvfLukeCardSet? Data, ImmutableArray<string> Issues)
{
    public const string Path = "etc/raid/luke/lukecardset.etc";
    public bool Ready => Data is not null && Issues.IsEmpty;

    public static LukeCardSetCatalog Load(PvfArchive pvf, RaidDungeonCatalog dungeons, DungeonRoomCatalog rooms)
    {
        if (!pvf.Contains(Path)) return new(null, ["luke-cardset-file-missing"]);
        try
        {
            var data = LukeCardSetFile.Read(pvf.ReadTokens(Path), pvf.ResolveString);
            return Validate(data, dungeons.Type == 1 ? id => dungeons.Find(id) is not null : _ => false,
                rooms.HasMonsterDefinition);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        { return new(null, ["luke-cardset-file-invalid:" + error.GetType().Name]); }
    }

    internal static LukeCardSetCatalog Validate(PvfLukeCardSet data, Func<int, bool> dungeonExists, Func<int, bool> monsterExists)
    {
        var issues = ImmutableArray.CreateBuilder<string>();
        foreach (int dungeon in data.Patterns.Select(row => row.Dungeon).Distinct())
            if (!dungeonExists(dungeon)) issues.Add($"luke-cardset-dungeon-unregistered:{dungeon}");
        foreach (var card in data.Cards)
            if (!monsterExists(card.Template)) issues.Add($"luke-cardset-monster-unregistered:{card.Template}");
        // IMAGE指向客户端资源，不伪装成PVF内文件，也不以未打包图片判断玩法可用。
        return new(data, issues.ToImmutable());
    }
}
