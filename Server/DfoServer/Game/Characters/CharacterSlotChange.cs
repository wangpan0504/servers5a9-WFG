namespace DfoServer.Game.Characters;

// 重排回执携带提交后的完整名册；ACK之后必须由同一次提交的名册覆盖客户端行序。
internal sealed record CharacterSlotChangeReceipt(CharacterRosterSnapshot Roster);

internal sealed record CharacterSlotChangeResult(CharacterSlotChangeReceipt? Receipt, long SelectionEpoch, string Reason)
{
    public bool Accepted => Receipt is not null;
}
