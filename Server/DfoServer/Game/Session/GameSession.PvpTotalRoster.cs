using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Social;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    bool CanConfigurePvpTotalRoster => CanRemainPvpMatching && pvpMatching is null;

    internal PvpTotalRosterSelection? CaptureStoredPvpTotalRoster(PvpTotalTeam team) => owner.Execute(() =>
    {
        if (!team.Complete || selectedRoster is not { } roster || !MatchesRoster(roster)) return null;
        var indices = ImmutableArray.CreateBuilder<byte>(3);
        foreach (var id in team.Characters)
        {
            int index = -1;
            for (int i = 0; i < roster.Entries.Length; i++)
                if (roster.Entries[i].Id == id) { index = i; break; }
            if (index < 0 || index > byte.MaxValue) return null;
            indices.Add((byte)index);
        }
        return CapturePvpTotalRoster(indices.MoveToImmutable());
    });

    // 发送泵只核对已捕获的发布身份，不在队列守卫中查询SQLite。
    internal bool CanPublishPvpTotalRoster(PvpTotalRosterSelection selection) => CanConfigurePvpTotalRoster &&
        ReferenceEquals(selection.Actor.Owner, owner) && ReferenceEquals(selection.Actor.Session, this) &&
        selection.Actor.IsCurrent() && ReferenceEquals(selection.Roster, selectedRoster) && MatchesRoster(selection.Roster);

    internal PvpTotalRosterSelection? CapturePvpTotalRoster(ImmutableArray<byte> indices, bool blocked = false) => owner.Execute(() =>
    {
        if (blocked || !CanConfigurePvpTotalRoster || selectedRoster is not { } roster || !MatchesRoster(roster) ||
            indices.IsDefault || indices.Length != 3 || indices.Distinct().Count() != 3 ||
            indices.Any(index => index >= roster.Entries.Length)) return null;

        // 026819F0全量NOTI2先清列表，02680B80逐行经03B75710追加；873引用该数组的位置。
        // 当前查询只核身份，不能重新排序后解释请求索引，也不加载未选中角色的完整资产。
        var current = repository.ListRoster(owner).ToDictionary(row => row.Id);
        var members = ImmutableArray.CreateBuilder<PvpTotalRosterMember>(3);
        foreach (byte index in indices)
        {
            var original = roster.Entries[index];
            if (!current.TryGetValue(original.Id, out var row) || !SameTotalRosterIdentity(original, row)) return null;
            members.Add(new(index, row));
        }
        if (members.Select(row => row.Character.Id).Distinct().Count() != 3) return null;
        return new PvpTotalRosterSelection(new SocialActor(owner, this, Selected!, SelectionEpoch), roster, members.MoveToImmutable());
    });

    internal bool CanUsePvpTotalRoster(PvpTotalRosterSelection selection, bool matching = false) => owner.Execute(() =>
    {
        if (!(matching ? CanRemainPvpMatching : CanConfigurePvpTotalRoster) || !ReferenceEquals(selection.Actor.Owner, owner) ||
            !ReferenceEquals(selection.Actor.Session, this) || !selection.Actor.IsCurrent() ||
            !ReferenceEquals(selection.Roster, selectedRoster) || !MatchesRoster(selection.Roster) ||
            selection.Members.IsDefault || selection.Members.Length != 3 ||
            selection.Members.Select(row => row.RosterIndex).Distinct().Count() != 3 ||
            selection.Members.Select(row => row.Character.Id).Distinct().Count() != 3) return false;

        var current = repository.ListRoster(owner).ToDictionary(row => row.Id);
        foreach (var member in selection.Members)
        {
            if (member.RosterIndex >= selection.Roster.Entries.Length ||
                !SameTotalRosterIdentity(selection.Roster.Entries[member.RosterIndex], member.Character) ||
                !current.TryGetValue(member.Character.Id, out var row) ||
                !SameTotalRosterIdentity(member.Character, row)) return false;
        }
        return true;
    });

    internal PvpTotalBattleRoster? CapturePvpTotalBattleRoster(PvpTotalTeam team, PvpTotalRosterSelection selection,
        SkillLearningCatalog skills, bool matching = false) => owner.Execute(() =>
    {
        if (!CanUsePvpTotalRoster(selection, matching) || !team.Complete ||
            !team.Characters.SequenceEqual(selection.Members.Select(row => (ushort?)row.Character.Id)) ||
            !team.Characters.Contains(Selected!.Id)) return null;
        var actors = ImmutableArray.CreateBuilder<Character>(3);
        foreach (var member in selection.Members)
        {
            var actor = Selected.Id == member.Character.Id ? Selected : repository.LoadCharacter(owner, member.Character.Slot);
            if (actor.Id != member.Character.Id || actor.Slot != member.Character.Slot || actor.Name != member.Character.Name ||
                actor.Job != member.Character.Job || actor.GrowType != member.Character.GrowType || actor.Level != member.Character.Level) return null;
            // 旧技能存档按正式初始化规则转换为只读战斗快照，不切换Session.Selected或保存其他角色。
            var initialized = SkillLearning.Initialize(actor, skills.Get);
            if (!initialized.Success) return null;
            var prepared=actor with {Learning=initialized.State!};
            actors.Add(prepared with {Learning=PvpTrainingSkills.ArenaSource(prepared,skills)});
        }
        return new PvpTotalBattleRoster(team, selection, actors.MoveToImmutable());
    });

    static bool SameTotalRosterIdentity(CharacterRosterEntry before, CharacterRosterEntry after) =>
        before.Id == after.Id && before.Slot == after.Slot && before.Name == after.Name;
}
