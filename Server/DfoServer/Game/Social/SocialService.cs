using DfoServer.Game.Session;

namespace DfoServer.Game.Social;

internal sealed class SocialService(ISocialStore store, OnlineSessionDirectory online, TimeProvider? clock = null)
{
    public SocialView? Capture(GameSession session)
    {
        if (session.CaptureSocialActor() is not { } actor) return null;
        var identity = session.PublicCharacter(actor.Character.Id);
        if (identity is null) return null;
        var characters = store.CharacterLinks(actor.Character.Id, false);
        var accounts = store.AccountFriends(actor.Owner.Account);
        var lastSeen = store.AccountLastSeenUnix(accounts.Select(link => link.Person.Account));
        long now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        // 所有目录查询、数据库读取及构包均在收发双方账号锁外完成。
        var current = online.Snapshot().Where(row => row.Ready).ToArray();
        SocialRow Row(SocialLink link, bool account)
        {
            var found = current.FirstOrDefault(row => account ? row.Account == link.Person.Account : row.CharacterId == link.Person.Id);
            var peer = found is null ? null : online.Find(found.Account)?.PublicCharacter(found.CharacterId);
            if (peer is null)
            {
                long elapsed = lastSeen.TryGetValue(link.Person.Account, out long observed) && observed > 0
                    ? Math.Max(0, now - observed) : 0;
                return new(link, identity.Channel.ServerIndex, 0, false, elapsed);
            }
            var c = peer.Character;
            var person = link.Person with { Id = c.Id, Name = c.Name, Level = c.Level, Job = c.Job, Grow = c.GrowType,
                Adventure = peer.AdventureName, AdventureLevel = peer.AdventureLevel };
            return new(link with { Person = person }, peer.Channel.ServerIndex, peer.Channel.Number, true);
        }
        return new(characters.Select(link => Row(link, false)).ToArray(), accounts.Select(link => Row(link, true)).ToArray(),
            store.AccountRequests(actor.Owner.Account), actor.IsCurrent);
    }

    public void Refresh(string account, ushort? changedCharacter = null, bool departed = false, Func<bool>? sourceCurrent = null)
    {
        if (online.Find(account) is { } session && Capture(session) is { } view && view.IsCurrent())
        {
            var recipientCurrent = view.IsCurrent;
            bool Current() => recipientCurrent() && (sourceCurrent?.Invoke() ?? true);
            // 旧连接离开后同角色已由新连接上线时，只恢复列表，不能再制造一次上线事件。
            if (departed && view.Characters.Any(row => row.Link.Person.Id == changedCharacter && row.Online))
                changedCharacter = null;
            if (Current()) session.SocialNoticeArrived?.Invoke(view with { ChangedCharacter = changedCharacter, IsCurrent = Current });
        }
    }

    public void PresenceChanged(ushort character, string account, bool departed = false)
    {
        // 必须先于Capture固定源身份；离线时也固定目录的空值，防止在途离线覆盖随后上线。
        var source = online.Find(account);
        var sourceCurrent = source?.CaptureSocialPresenceGuard();
        bool Current() => ReferenceEquals(online.Find(account), source) && (sourceCurrent?.Invoke() ?? true);
        foreach (string observer in store.SocialObservers(character, account)) Refresh(observer, character, departed, Current);
    }
}
