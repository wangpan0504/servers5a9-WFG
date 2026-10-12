namespace DfoServer.Game.Characters;

// 删除只需要名册身份和版本，不装载或改写完整角色与账号资产。
internal sealed record CharacterDeletion(ushort Id, ushort Slot, string Name, long Revision)
{
    public bool AdventureBusy { get; init; }
}

internal sealed record CharacterDeletionReceipt(ushort RosterIndex, CharacterDeletion Deleted, CharacterRosterSnapshot Roster);

internal sealed record CharacterDeletionResult(CharacterDeletionReceipt? Receipt, long SelectionEpoch, string Reason)
{
    public bool Accepted => Receipt is not null;
}
