using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public AdventureCapsuleCommit CommitAdventureCapsule(AccountOwners.Lease owner, AdventureCapsulePlan plan,
        AdventureCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
    {
        var expected = plan.Expected; var growth = plan.Growth;
        plan.After.Validate(catalog);
        var allowed = plan.Before.State with { CapsuleExperience = plan.After.CapsuleExperience };
        if (JsonSerializer.Serialize(allowed) != JsonSerializer.Serialize(plan.After) || expected.DungeonState is null ||
            expected.Level < catalog.Capsule.MinimumLevel || expected.Level > catalog.Capsule.MaximumLevel ||
            growth.CharacterId != expected.Id || growth.Revision != expected.Revision || !SameLearning(growth.Before, expected.Learning) ||
            growth.Learning.Experience - growth.Before.Experience != catalog.Capsule.GrantedExperience ||
            plan.Before.State.CapsuleExperience < catalog.Capsule.GaugeExperience ||
            plan.After.CapsuleExperience != plan.Before.State.CapsuleExperience - catalog.Capsule.GaugeExperience)
            throw new InvalidDataException("胶囊计划的账号扣除或角色成长越界");
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitAdventureCapsule", deferred: true);
        using var tx = write.Transaction;
        var current = ReadAdventure(db, tx, owner.Account, catalog, now);
        if (!current.Members.SequenceEqual(plan.Before.Members) || JsonSerializer.Serialize(current.State) != JsonSerializer.Serialize(plan.Before.State))
            throw new InvalidOperationException("胶囊规划后账号或角色名册已变化");
        var character = Require(db, tx, owner.Account, expected.Name);
        if (character.Id != expected.Id || character.Revision != expected.Revision || character.Level != expected.Level ||
            character.GrowType != expected.GrowType || character.DungeonState is null || !SameLearning(character.Learning, expected.Learning))
            throw new InvalidOperationException("胶囊角色快照已变化");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE characters SET level=$level,experience=$experience,revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        command.Parameters.AddWithValue("$level", growth.TargetLevel); command.Parameters.AddWithValue("$experience", checked((long)growth.Learning.Experience));
        command.Parameters.AddWithValue("$id", expected.Id); command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$revision", expected.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("胶囊角色revision失配");
        WriteLearning(db, tx, expected.Id, growth.Learning); WriteCanonicalSkills(db, tx, expected.Id, growth.Learning);
        // 成长胶囊给的角色经验同样累积熟练度。
        var proficiencyGain = ProficiencyProgression.Gain(growth.PreviousLevel, growth.Before.Experience,
            checked((uint)growth.RequestedExperience), growth.Catalog);
        if (ProficiencyProgression.Advance(new(character.DungeonState.ProficiencyLevel, character.DungeonState.ProficiencyExperience),
                proficiencyGain, growth.Catalog) is { } proficiency)
            WriteProficiency(db, tx, owner.Account, expected.Id, proficiency.Before, proficiency.After);
        if (quests is not null) WriteQuestConditions(db, tx, expected.Id, character.ActiveMainQuests,
            QuestStateProgress.Refresh(character.ActiveMainQuests, character with { Level = growth.TargetLevel, Learning = growth.Learning }, quests));
        // 成长胶囊兑换出的角色经验按国服规则不计入冒险团，只推进角色里程；排除额并入同一次余额写入。
        var afterState = plan.After;
        if (CreditAdventure(db, tx, owner.Account, expected.Id, AdventureCreditSource.ExcludedGrant,
                growth.Learning.Experience - growth.Before.Experience,
                EffectivePosition(character.Level, character.Learning.Experience), character.Level, growth.TargetLevel,
                now) is { } credit)
            afterState = afterState with { ExcludedExperience = credit.After.ExcludedExperience };
        WriteAdventureBalances(db, tx, owner.Account, current.State, afterState, catalog);
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO account_adventure_receipts VALUES($account,$event,$character,'capsule_claim',0,$before,$revision)";
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$character", expected.Id); command.Parameters.AddWithValue("$before", current.State.Revision);
        command.Parameters.AddWithValue("$revision", current.State.Revision + 1); command.ExecuteNonQuery();
        ApplyBattlePassExperienceGain(db, tx, owner.Account, "adventure_capsule",
            FormattableString.Invariant($"{expected.Id}:{current.State.Revision}:{expected.Revision}"), growth.GainedExperience, now);
        var committed = ReadAdventure(db, tx, owner.Account, catalog, now);
        character = Require(db, tx, owner.Account, expected.Name);
        tx.Commit(); return new AdventureCapsuleCommit(committed, character);
    });
}
