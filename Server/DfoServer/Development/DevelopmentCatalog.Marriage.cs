using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer.Development;

internal sealed partial class DevelopmentCatalog
{
    public bool MarriageMale(byte job)
    {
        var list = CharacterCatalog.ReadJobList(pvf);
        if (!list.TryGetValue(job, out var path)) throw new InvalidDataException("当前PVF没有该职业，未建立婚姻关系");
        string identity = PvfScript.Section(pvf.ReadText("character/" + path), "job").Trim().Trim('`').Trim().Trim('[', ']');
        return MarriageDirectRules.Male(identity);
    }
}
