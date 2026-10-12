using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal sealed class CharacterEmotionCatalog
{
    public const string ScriptPath = "etc/emotionexpressiondata.etc";
    readonly FrozenSet<ushort> basic;

    public CharacterEmotionCatalog(IEnumerable<EmotionExpressionDefinition> definitions) => basic = definitions
        .Where(d => d.Kind is >= 0 and <= 2 && d.Visible)
        .Select(d => checked((ushort)d.Id)).ToFrozenSet();

    public bool CanSelect(ushort id) => basic.Contains(id);
    public static CharacterEmotionCatalog Load(PvfArchive pvf) => new(pvf.FindFile(ScriptPath) is null ? [] :
        EmotionExpressionFile.Read(pvf.ReadTokens(ScriptPath), pvf.ResolveString));
}
