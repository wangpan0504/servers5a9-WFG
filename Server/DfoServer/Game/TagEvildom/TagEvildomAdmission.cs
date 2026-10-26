using DfoServer.Game.Characters;

namespace DfoServer.Game.TagEvildom;

// 与正式尝试号在同一事务固定；准入不计通关额度，也不是领取奖励的凭据。
internal sealed record TagEvildomAdmission(TagEvildomConfiguration Configuration, Character Primary, Character Standby,
    int DungeonId, string ResourceKey, long AdmittedAt)
{
    public int TeamIndex => Configuration.SelectedTeam;
    public TagEvildomTeam Team => Configuration.Teams[TeamIndex];
    public void Validate()
    {
        Configuration.Validate();
        new TagEvildomParticipants(Primary, Standby, Configuration.Revision).Validate();
        if (Team != new TagEvildomTeam(Primary.Id, Standby.Id) || DungeonId is not (9608 or 400000095) ||
            ResourceKey.Length != 64 || ResourceKey.Any(value => !char.IsAsciiHexDigitLower(value)) || AdmittedAt < 0)
            throw new InvalidDataException("再战准入必须固定完整队首、备用及本次资源身份");
    }
}
