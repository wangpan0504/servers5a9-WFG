using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;

namespace DfoServer.Game.Social;

internal sealed record SocialPerson(string Account, ushort Id, string Name, byte Level, byte Job, byte Grow,
    string Adventure, int AdventureLevel);
internal sealed record SocialLink(SocialPerson Person, DateTimeOffset Created, string Memo = "", byte Group = 0);
internal sealed record AccountFriendRequest(SocialPerson Person, bool Outgoing, string Message, long Created);
internal sealed record AccountFriendGroup(byte Icon, string Name)
{
    public static AccountFriendGroup[] Defaults() => [new(0, ""), new(1, ""), new(2, ""), new(3, "")];
    public static bool Valid(AccountFriendGroup[] groups)
    {
        if (groups.Length != 4) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group.Icon > 7 || group.Name.Length == 0 || group.Name[0] == ' ' || group.Name[^1] == ' ' ||
                group.Name.Any(c => char.IsControl(c) || c is '\'' or '%') || !names.Add(group.Name)) return false;
            try { if (CharacterName.Encoding.GetByteCount(group.Name) > 10) return false; }
            catch (System.Text.EncoderFallbackException) { return false; }
        }
        return true;
    }
}
internal sealed record SocialRow(SocialLink Link, byte Server, ushort Channel, bool Online,
    long OfflineElapsedSeconds = 0);
internal sealed record SocialView(SocialRow[] Characters, SocialRow[] Accounts, AccountFriendRequest[] Requests,
    Func<bool> IsCurrent, ushort? ChangedCharacter = null);
internal sealed record SocialResult(ushort Error, SocialPerson? Person = null)
{
    public bool Accepted => Error == 0;
}
internal sealed record SocialActor(AccountOwners.Lease Owner, GameSession Session, Character Character, long Epoch)
{
    public bool IsCurrent() => Owner.IsCurrent && Session.Ready && Session.SelectionEpoch == Epoch &&
        Session.Selected?.Id == Character.Id;
    public T Execute<T>(Func<T> action) => Owner.Execute(() =>
        IsCurrent() ? action() : throw new InvalidOperationException("Stale social character"));
}

internal interface ISocialStore
{
    AccountFriendGroup[] AccountGroups(SocialActor actor);
    void SaveAccountGroups(SocialActor actor, AccountFriendGroup[] groups);
    SocialPerson? FindSocialPerson(string name, bool adventure = false);
    SocialLink[] CharacterLinks(ushort owner, bool blocked);
    SocialResult ChangeCharacterLink(SocialActor actor, string name, bool blocked, bool remove, string memo, DateTimeOffset now);
    SocialLink[] AccountFriends(string account);
    IReadOnlyDictionary<string, long> AccountLastSeenUnix(IEnumerable<string> accounts);
    AccountFriendRequest[] AccountRequests(string account);
    SocialResult RequestAccountFriend(SocialActor actor, string adventure, string message, byte group, string memo, DateTimeOffset now);
    SocialResult ResolveAccountFriend(SocialActor actor, string adventure, bool accept, bool remove, bool cancel, string memo = "", byte group = 0);
    SocialResult UpdateSocialMemo(SocialActor actor, string name, string memo, bool blocked, byte group = 0);
    HashSet<ushort> BlockingCharacters(ushort sender);
    bool IsBlocked(ushort recipient, ushort sender);
    string[] SocialObservers(ushort character, string account);
}
