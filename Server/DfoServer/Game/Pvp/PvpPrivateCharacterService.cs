using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Pvp;

// Runtime显式共享不可变资源；规划在账号锁外完成，不在Session里另建目录。
internal sealed class PvpPrivateCharacterService(CharacterCatalog characters,PvpPrivateCharacterCatalog catalog,
    SkillLearningCatalog learning,CharacterGrowthCatalog growth,ItemCatalog items,TownCatalog towns)
{
    public bool TryPrepare(string name,byte job,byte grow,DateTimeOffset now,out NewCharacter? plan,out string reason)
    {
        plan=null;reason="unsupported-resource";
        if(!catalog.TryGet(job,grow,out var definition)) return false;
        try
        {
            var location=ChannelAreaLanding.DecideArea(towns,10,0,85,new HashSet<int>());
            if(location.Spot is not {} spot) {reason=location.Reason;return false;}
            plan=PvpPrivateCharacterPlan.Prepare(name,characters.Get(job),definition,catalog.AccountLevelRequirement,
                spot,learning,growth,items,now);
            reason="ready";return true;
        }
        catch(Exception error) when(error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException)
        {reason=error.Message;return false;}
    }
}
