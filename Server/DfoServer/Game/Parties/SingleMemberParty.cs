namespace DfoServer.Game.Parties;

internal sealed record PartySettings(byte TitlePreset, string? Title, byte MemberCode, uint SelectionId,
    byte SelectionCode, ushort SelectionValue, byte RecruitFlag, byte TargetMode, ushort TargetId, byte Extra);
internal sealed record SoloPartyRequest(byte Mode, PartySettings Settings);

// 当前仅有队长本人；用其唯一角色ID作为本会话的队伍键，不建立另一套在线成员目录。
internal sealed record SingleMemberParty(ushort LeaderId, PartySettings Settings)
{
    public ushort Id => LeaderId;
}
internal sealed record SoloPartyReceipt(SingleMemberParty Party, long Generation, long SelectionEpoch, bool Created,
    bool Removed = false, long Version = 0);
