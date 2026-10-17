using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Game.Auth;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;

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

internal sealed partial class GameSession(ICharacterRepository repository, TownCatalog towns, AccountOwners.Lease owner, AuthenticatedIdentity? identity = null,
    DfoServer.GameWorld.Quests.QuestCatalog? questCatalog = null, GameDebugSettings? debugSettings = null, GameWorld.Channels.ChannelEntry? entry = null,
    DfoServer.Game.Quests.QuestSkipEffects? questEffects = null, PremiumContractCatalog? contracts = null, TimeProvider? timeProvider = null,
    DfoServer.Game.Dungeons.DungeonPassiveDropPolicy? passiveDrops = null,
    Game.Creatures.CreatureService? creatures = null)
{
    public AuthenticatedIdentity? Identity { get; } = identity;
    public GameWorld.Channels.ChannelEntry? ChannelEntry { get; } = entry;
    public Character? Selected { get; private set; }
    public bool Ready { get; private set; }
    public SessionPhase Phase { get; private set; } = SessionPhase.Unselected;
    public long SelectionEpoch { get; private set; }
    public string TravelReason { get; private set; } = "not-requested";

    public CharacterFatigueState FatigueForClient(Character character)
    {
        var state = (character.Fatigue ?? CharacterFatigueState.NewOrdinary)
            .ForBlackDiamond(character.BlackDiamondExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        state.Validate();
        return debugSettings?.InfiniteFatigue == true ? state with { Used = 0, Remaining = state.Maximum } : state;
    }

    IReadOnlySet<int> completed = new HashSet<int>();
    public CharacterRosterEntry[] Roster() => repository.ListRoster(owner);
    public Character[] QueryCharacters() => repository.List(owner);

    internal OnlineCharacterSnapshot? OnlineCharacter()
    {
        try
        {
            return owner.Execute(() => Selected is { } character
                ? new OnlineCharacterSnapshot(owner.Account, character.Id, character.Name, Ready, Phase, ReadState(Phase)) : null);
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        {
            return null;
        }
    }

    public bool NameExists(string name) => owner.Execute(() =>
    {
        if (Selected is not null)
            throw new InvalidDataException("Name check after selection");
        return repository.NameExists(name);
    });

    public Character CreateCharacter(string name, CharacterProfile profile, SkillLearningCatalog? learning = null,
        TutorialFlow? tutorial = null) => CreateCharacter(name, profile, learning is null ? null : learning.Get, tutorial);

    public CharacterCreationReceipt CreateCharacterForClient(string name, CharacterProfile profile, SkillLearningCatalog? learning = null,
        TutorialFlow? tutorial = null) => PrepareCharacterCreation(name, profile, learning is null ? null : learning.Get, tutorial);

    internal Character CreateCharacter(string name, CharacterProfile profile, Func<byte, ushort, SkillLearningDefinition>? definition,
        TutorialFlow? tutorial = null) => PrepareCharacterCreation(name, profile, definition, tutorial).Created;

    CharacterCreationReceipt PrepareCharacterCreation(string name, CharacterProfile profile, Func<byte, ushort, SkillLearningDefinition>? definition,
        TutorialFlow? tutorial = null)
    {
        long epoch = owner.Execute(() =>
        {
            if (Selected is not null)
                throw new InvalidDataException("Create after selection");
            return SelectionEpoch;
        });
        var creation = CharacterCreation.Prepare(name, profile, towns.Room, towns.InitialReturn, definition, tutorial);
        return owner.Execute(() =>
        {
            if (Selected is not null || SelectionEpoch != epoch)
                throw new InvalidOperationException("Stale character creation state");
            var created = repository.Create(owner, creation);
            SelectionEpoch++;
            return new CharacterCreationReceipt(created, CaptureRoster());
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

    public void Select(ushort slot, GoldLimitCatalog? goldLimits = null, bool resumeTown = false) => SelectCharacter(slot, goldLimits, null, resumeTown);

    void SelectCharacter(ushort slot, GoldLimitCatalog? goldLimits, CharacterRosterEntry? expected, bool resumeTown = false)
    {
        owner.Execute(() =>
        {
            if (Selected is not null)
                throw new InvalidDataException("Invalid selection state");
            var character = repository.LoadCharacter(owner, slot);
            if (expected is not null && (character.Id != expected.Id || character.Slot != expected.Slot || character.Name != expected.Name))
                throw new InvalidOperationException("Selection published roster identity changed");
            character = repository.GrantBlackDiamondFatigue(owner, character, DateTimeOffset.UtcNow);
            if (questCatalog is not null && character.QuestCycleDay != DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow))
                character = repository.AdvanceQuestCycle(owner, character, DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow));
            var quests = DfoServer.Game.Quests.QuestEligibility.Completed(character);
            character = CharacterSelection.Prepare(repository, owner, character, towns, quests, goldLimits, resumeTown);
            Selected = character;
            completed = quests;
            Ready = false;
            SetState(SessionPhase.Unselected);
            SelectionEpoch++;
            return true;
        });
    }

    public bool CanPublishCreation(CharacterCreationReceipt receipt) => owner.Execute(() =>
        MatchesRoster(receipt.Roster) &&
        Selected is null && Phase == SessionPhase.Unselected);

    public void MarkReady()
    {
        owner.Execute(() =>
        {
            if (Selected is null)
                throw new InvalidOperationException("No selected character");
            if (Ready || Phase != SessionPhase.Unselected)
                throw new InvalidOperationException("入城就绪只能由当前未完成的选角初始化推进");
            Ready = true;
            SetState(SessionPhase.Town);
            return true;
        });
    }

    public Character UpdateProgress(CharacterProgressEdit request) => owner.Execute(() =>
    {
        if (pendingRental is not null)
            throw new InvalidOperationException("租赁结果待确认，须恢复原提交或重新选角");
        if (HasPendingFormalCard && string.Equals(Selected?.Name, request.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("翻牌结果待确认，不能覆盖当前角色进度");
        if (request.Account != owner.Account)
            throw new InvalidDataException("账号与当前会话不一致");
        // 达人契约属于账号；即使命令目标是另一角色，也会替换当前技能快照。
        if (HasPendingTownQuestCommit && (request.Field == CharacterProgressField.ExpertContract ||
            string.Equals(Selected?.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("任务结果待确认，不能覆盖当前角色进度");
        var updated = repository.UpdateProgress(owner, request);
        // 提交成功后才替换内存。任务集合立即参与城镇准入；客户端画面仍等下次选角。
        if (Selected?.Id == updated.Id)
        {
            InvalidateImageCommunication();
            Selected = updated;
            completed = DfoServer.Game.Quests.QuestEligibility.Completed(updated);
        }
        else if (request.Field == CharacterProgressField.ExpertContract && Selected is { } selected)
            Selected = selected with { Learning = selected.Learning with { ExpertContractExpiresAt = updated.Learning.ExpertContractExpiresAt } };

        return updated;
    });

    public Character ResetProgress(CharacterProgressEdit request, CharacterProgressReset reset)
    {
        var captured = owner.Execute(() =>
        {
            if (pendingRental is not null)
                throw new InvalidOperationException("租赁结果待确认，须恢复原提交或重新选角");
            if (request.Account != owner.Account) throw new InvalidDataException("账号与当前会话不一致");
            if (HasPendingTownQuestCommit && string.Equals(Selected?.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("任务结果待确认，不能重置当前角色进度");
            var target = repository.ListRoster(owner).SingleOrDefault(value => string.Equals(value.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("角色不存在");
            var character = repository.LoadCharacter(owner, target.Slot);
            return (Character: character, Selected, SelectionEpoch);
        });
        var plan = reset.Prepare(captured.Character, request);
        return owner.Execute(() =>
        {
            if (pendingRental is not null || !ReferenceEquals(Selected, captured.Selected) || SelectionEpoch != captured.SelectionEpoch ||
                (HasPendingFormalCard || HasPendingTownQuestCommit) && Selected?.Id == captured.Character.Id)
                throw new InvalidOperationException("在线角色快照已变化，请重试");
            var updated = repository.ResetProgress(owner, plan);
            if (Selected?.Id == updated.Id)
            {
                InvalidateImageCommunication();
                Selected = updated;
            }
            return updated;
        });
    }

    void Invalidate(string reason)
    {
        // 原实例失效后，不让其迟到回执找到同账号的新Selected。
        formalDungeon = null;
        ClearConsumableUse();
        publishedRoster = null;
        pendingDungeonContinuation = null;
        pendingQuestReplay = null;
        pendingQuestCompletion = null;
        pendingQuestLifecycle = null;
        pendingRental = null;
        InvalidateImageCommunication();
        pendingQuestQuickClear = null;
        SelectionEpoch++;
        Selected = null;
        completed = new HashSet<int>();
        Ready = false;
        CurrentDungeonSelection = null;
        SetState(SessionPhase.Unselected);
        TravelReason = reason;
    }
}
