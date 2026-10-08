using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Game.Auth;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

// 角色会话阶段由Session维护，正式副本按其私有实例推进。
internal enum SessionPhase : byte
{
    Unselected,
    Town,
    MapSelect,
    Loading,
    Dungeon,
    Settlement
}

internal sealed partial class GameSession(ICharacterRepository repository, TownCatalog towns, AccountOwners.Lease owner, AuthenticatedIdentity? identity = null)
{
    public AuthenticatedIdentity? Identity { get; } = identity;
    public Character? Selected { get; private set; }
    public bool Ready { get; private set; }
    public SessionPhase Phase { get; private set; } = SessionPhase.Unselected;
    public long SelectionEpoch { get; private set; }
    public string TravelReason { get; private set; } = "not-requested";

    IReadOnlySet<int> completed = new HashSet<int>();
    public CharacterRosterEntry[] Roster() => repository.ListRoster(owner);
    public Character[] QueryCharacters() => repository.List(owner);

    public bool NameExists(string name) => owner.Execute(() =>
    {
        if (Selected is not null)
            throw new InvalidDataException("Name check after selection");
        return repository.NameExists(name);
    });

    public Character CreateCharacter(string name, CharacterProfile profile, SkillLearningCatalog? learning = null) =>
        CreateCharacter(name, profile, learning is null ? null : learning.Get);

    internal Character CreateCharacter(string name, CharacterProfile profile, Func<byte, ushort, SkillLearningDefinition>? definition)
    {
        long epoch = owner.Execute(() =>
        {
            if (Selected is not null)
                throw new InvalidDataException("Create after selection");
            return SelectionEpoch;
        });
        var creation = CharacterCreation.Prepare(name, profile, towns.Room, towns.InitialReturn, definition);
        return owner.Execute(() =>
        {
            if (Selected is not null || SelectionEpoch != epoch)
                throw new InvalidOperationException("Stale character creation state");
            return repository.Create(owner, creation);
        });
    }

    public void ReturnToRoster()
    {
        owner.Execute(() =>
        {
            // 移动和传送已逐次提交；这里只结束当前角色会话，不重写位置或进度。
            Invalidate("not-requested");
            return true;
        });
    }

    public void End()
    {
        // 断线时旧 generation 已不能拿锁，仍要让这个会话自己的代数失效。
        try
        {
            owner.Execute(() =>
            {
                Invalidate("session-ended");
                return true;
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "Stale account generation")
        {
            Invalidate("session-ended");
        }
    }

    public bool MatchesSelection(long epoch, ushort characterId) =>
        Phase == SessionPhase.Town && SelectionEpoch == epoch && Selected?.Id == characterId;

    public void Select(ushort slot, GoldLimitCatalog? goldLimits = null)
    {
        owner.Execute(() =>
        {
            if (Selected is not null)
                throw new InvalidDataException("Invalid selection state");
            var character = repository.LoadCharacter(owner, slot);
            var quests = character.CompletedQuests.ToHashSet();
            character = CharacterSelection.Prepare(repository, owner, character, towns, quests, goldLimits);
            Selected = character;
            completed = quests;
            Ready = false;
            Phase = SessionPhase.Unselected;
            SelectionEpoch++;
            return true;
        });
    }

    public void MarkReady()
    {
        owner.Execute(() =>
        {
            if (Selected is null)
                throw new InvalidOperationException("No selected character");
            Ready = true;
            Phase = SessionPhase.Town;
            return true;
        });
    }

    public Character UpdateProgress(CharacterProgressEdit request) => owner.Execute(() =>
    {
        if (request.Account != owner.Account)
            throw new InvalidDataException("账号与当前会话不一致");
        var updated = repository.UpdateProgress(owner, request);
        // 提交成功后才替换内存。任务集合立即参与城镇准入；客户端画面仍等下次选角。
        if (Selected?.Id == updated.Id)
        {
            Selected = updated;
            completed = updated.CompletedQuests.ToHashSet();
        }
        else if (request.Field == CharacterProgressField.ExpertContract && Selected is { } selected)
            Selected = selected with { Learning = selected.Learning with { ExpertContractExpiresAt = updated.Learning.ExpertContractExpiresAt } };

        return updated;
    });

    public Character ResetProgress(CharacterProgressEdit request, CharacterProgressReset reset)
    {
        var captured = owner.Execute(() =>
        {
            if (request.Account != owner.Account) throw new InvalidDataException("账号与当前会话不一致");
            var target = repository.ListRoster(owner).SingleOrDefault(value => string.Equals(value.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("角色不存在");
            var character = repository.LoadCharacter(owner, target.Slot);
            return (Character: character, Selected, SelectionEpoch);
        });
        var plan = reset.Prepare(captured.Character, request);
        return owner.Execute(() =>
        {
            if (!ReferenceEquals(Selected, captured.Selected) || SelectionEpoch != captured.SelectionEpoch)
                throw new InvalidOperationException("在线角色快照已变化，请重试");
            var updated = repository.ResetProgress(owner, plan);
            if (Selected?.Id == updated.Id) Selected = updated;
            return updated;
        });
    }

    void Invalidate(string reason)
    {
        // 原实例失效后，不让其迟到回执找到同账号的新Selected。
        formalDungeon = null;
        pendingQuestCompletion = null;
        SelectionEpoch++;
        Selected = null;
        completed = new HashSet<int>();
        Ready = false;
        Phase = SessionPhase.Unselected;
        CurrentDungeonSelection = null;
        TravelReason = reason;
    }
}
