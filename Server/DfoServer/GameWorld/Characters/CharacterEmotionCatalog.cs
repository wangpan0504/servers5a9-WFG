using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal sealed record CharacterEmotionDefinition(int Kind, bool Visible, ImmutableArray<int> RequiredQuests);

internal sealed class CharacterEmotionCatalog
{
    public const string ScriptPath = "etc/emotionexpressiondata.etc";
    readonly FrozenSet<ushort> basic;
    readonly FrozenDictionary<ushort, CharacterEmotionDefinition> definitions;

    public CharacterEmotionCatalog(IEnumerable<EmotionExpressionDefinition> source)
    {
        definitions = source.ToFrozenDictionary(d => checked((ushort)d.Id),
            d => new CharacterEmotionDefinition(d.Kind, d.Visible, d.RequiredQuests.IsDefault ? [] : d.RequiredQuests));
        basic = definitions.Where(pair => pair.Value.Kind is >= 0 and <= 2 && pair.Value.Visible)
            .Select(pair => pair.Key).ToFrozenSet();
    }

    public bool CanSelect(ushort id) => basic.Contains(id);
    public CharacterEmotionDefinition? Find(ushort id) => definitions.GetValueOrDefault(id);
    public static CharacterEmotionCatalog Load(PvfArchive pvf) => new(pvf.FindFile(ScriptPath) is null ? [] :
        EmotionExpressionFile.Read(pvf.ReadTokens(ScriptPath), pvf.ResolveString));
}
