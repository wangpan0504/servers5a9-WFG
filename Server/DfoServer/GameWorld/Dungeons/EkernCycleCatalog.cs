using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 匹配资源只启用艾肯48组；其他循环组不借此规则放行。
internal sealed record EkernCycleCatalog(int NormalLimit, int TrialLimit)
{
    public const string Path = "etc/dungeoncyclesystem.etc";
    public static EkernCycleCatalog Load(PvfArchive archive) => Read(archive.ReadTokens(Path), archive.ResolveString);
    internal static EkernCycleCatalog Read(IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        if (tokens.Count != 10 || tokens[0].Type != 3 || resolve(tokens[0].Value) != "[dungeon cycle]" ||
            tokens[^1].Type != 3 || resolve(tokens[^1].Value) != "[/dungeon cycle]" ||
            tokens.Skip(1).Take(8).Any(token => token.Type != 0) ||
            tokens.Skip(1).Take(8).Select(token => token.Value).ToArray() is not [48,-1,5097,3,48,-1,5098,1])
            throw new InvalidDataException("艾肯循环副本资源合同未确认");
        return new(3,1);
    }
    public bool CanEnter(uint dungeon, int normal, int trial) => dungeon switch
    {
        5097 => normal < NormalLimit,
        5098 => normal >= NormalLimit && trial < TrialLimit,
        _ => true
    };
}
