using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestAntonUnlockTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        QuestAntonUnlockTests.RunResources(resources.Towns, resources.Characters, resources.ItemResources,
            resources.Learning, resources.Growth, SelfTestCatalogs.QuestRewardParameters(resources.Archive), resources.Quests);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "quest-anton-unlock"), resources: resources);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        ushort id;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("AntonWire", resources.Characters.Get(0), QuestAntonUnlockTests.Nain(resources.Towns),
                resources.Towns.InitialReturn, resources.Learning.Get);
            var character = repository.Create(owner, candidate with
            {
                Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [3779],
                InitialLearning = SkillPointLedger.Synchronize(candidate.Job, candidate.GrowType, 85,
                    candidate.InitialLearning with { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning)
            });
            id = character.Id;
            repository.CommitInventory(owner, character, new InventoryPlan(character.Inventory, character.Inventory with
                { AccountWallet = character.Inventory.AccountWallet.Set(CurrencyKind.GoldCube, 100) }, "anton_wire_funding"));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true, resumeTown: true, room: false);
            That(QuestLifecycleTcpTests.List(peer.AvailableQuests, 85).Contains(12829), "NOTI21包含合格安徒恩解锁候选");
            peer.Send(31, [31, 0, 29, 50]);
            var accepted = peer.Expect(1, 31).Body;
            That(accepted.Length == 8 && accepted[0] == 1 && U16(accepted, 1) == 12829 && U32(accepted, 3) == 0, "CMD31原生接取成功");
            peer.Barrier(packet => That(packet.Opcode is 21 or 356, "接取仅允许任务候选和完成历史投影"));
            peer.Send(34, [34, 0, 29, 50, 255, 255, 1, 0, 255, 255]);
            var finished = peer.Expect(1, 34).Body;
            That(finished.Length > 10 && finished[0] == 1 && U16(finished, 1) == 12829,
                "CMD34发布原寻物交付和四项真实奖励");
            peer.Expect(0, 14);
            peer.Expect(0, 37);
            peer.Expect(0, 21);
            var completed = peer.Expect(0, 356).Body;
            That(completed[4 + 12829] != 0 && fixture.Query(
                "SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=12829") is [[1L]],
                "NOTI356与真实完成回执一致");
            peer.Send(34, [34, 0, 29, 50, 255, 255, 1, 0, 255, 255]); peer.Expect(1, 34, [0, 19, 0]);
            peer.Barrier();
            peer.Send(3, [0]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = repository.LoadCharacter(owner, 0);
            That(saved.Id == id && saved.Inventory.AccountWallet.Get(CurrencyKind.GoldCube) == 0 &&
                CharacterRosterEntry.FromCharacter(saved).AntonEligible &&
                saved.Inventory.Items.Count(row => row.Core.ItemId is 10095688 or 10095689 or 10095690 or 10095687 && row.Uid > 0) == 4,
                "网络事务扣100账号钱包并持久四项UID奖励和频道资格");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true, resumeTown: true, room: false);
            That(!QuestLifecycleTcpTests.List(peer.AvailableQuests, 85).Contains(12829), "重连21不再给已完成任务");
            peer.Send(31, [31, 0, 29, 50]); peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=12829") is [[1L]],
                "重连重放31不重复发奖");
        }
    }
}
