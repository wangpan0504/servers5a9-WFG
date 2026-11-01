using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class ExpertJobHandler
{
    async Task<CommandResult> TradeStoreAsync(GameConnectionContext context, ushort actor, byte job,
        Func<Character, ExpertJobActionPlan?> planAction)
    {
        var opcode = job == 3 ? CmdPacketType.REQUEST_DISJOINT_ITEM : CmdPacketType.USE_ENCHANT_STORE;
        async Task<CommandResult> Reject()
        {
            await context.Reply(opcode, ExpertJobPackets.Fail(19), "expert_job_trade_rejected");
            return CommandResult.Handled;
        }
        if (context.BlocksTownActions || online?.FindConnectionByCharacterId(actor) is not { ExpertJob: { } transport } connection ||
            ReferenceEquals(connection.Session, context.Session) || connection.Session.ExpertJobOwner.Account == context.Owner.Account ||
            stores is null || presence is null || !stores.TryGet(actor, out var store) || store.JobType != job ||
            !transport.TryReserve()) return await Reject();
        // 当前顾客命令已持本连接门禁，店主只尝试非阻塞保留，禁止两名顾客互相等待形成环。
        // 保留覆盖规划、提交和双方发布；断线、GM 换角仍由 lease/快照守卫拒绝。
        try
        {
            var provider = connection.Session;
            if (transport.BlocksActions() || context.Session.CaptureExpertJobTrade() is not { } customer ||
                provider.CaptureExpertJobTrade() is not { } owner || owner.Id != actor || owner.ExpertJobType != job ||
                !provider.ExpertJobStoreOpen || !context.Session.MatchesExpertJobStoreQuote(store) || !presence.CanInteract(context.Owner, customer.Id, provider.ExpertJobOwner, owner.Id))
                return await Reject();
            // 顾客提供材料和装备，店主提供资格/熟练度/机器。规划保留顾客主职业、等级和物品资格。
            var candidate = customer with
            {
                ExpertJobType = owner.ExpertJobType, ExpertJobRecipes = owner.ExpertJobRecipes,
                ExpertJobExperience = owner.ExpertJobExperience, ExpertMachineGrade = owner.ExpertMachineGrade,
                ExpertMachineEndurance = owner.ExpertMachineEndurance
            };
            if (planAction(candidate) is not { } action) return await Reject();
            uint fee = store.First;
            if (fee > 100000000 || action.Inventory.After.Gold < fee ||
                (long)owner.Inventory.Gold + fee > owner.Inventory.GoldCarryLimit) return await Reject();
            long netGold = (long)action.Inventory.After.Gold - customer.Inventory.Gold;
            var customerInventory = new InventoryPlan(customer.Inventory,
                action.Inventory.After with { Gold = action.Inventory.After.Gold - fee }, "expert_job_trade_customer",
                action.Inventory.CharacterPrecondition);
            var ownerInventory = new InventoryPlan(owner.Inventory,
                owner.Inventory with { Gold = checked(owner.Inventory.Gold + fee) }, "expert_job_trade_owner", new(owner));
            var trade = new ExpertJobTradePlan(customer, owner, customerInventory, ownerInventory, action.Experience,
                action.Endurance, fee, netGold < 0 ? checked((uint)-netGold) : 0, netGold > 0 ? checked((uint)netGold) : 0);
            var committed = GameSession.CommitExpertJobTrade(context.Session, provider, trade, () =>
                !context.Stop.IsCancellationRequested && !context.BlocksTownActions && !transport.BlocksActions() &&
                stores.TryGet(actor, out var currentStore) && ReferenceEquals(currentStore, store) &&
                context.Session.MatchesExpertJobStoreQuote(store) &&
                presence.CanInteract(context.Owner, customer.Id, provider.ExpertJobOwner, owner.Id));
            if (committed is not { } results || results.Customer.Receipt is not { } receipt || results.Owner.Receipt is not { } ownerReceipt)
                return await Reject();
            stores.TryUpdate(store, store.WithStatus(ownerReceipt.Character, scripts));
            bool Current() => context.Session.CanPublishInventory(receipt);
            // 提交后的两端发布独立开始；一端断线不能跳过另一端，发布失败绝不回到 SQLiteBusyRetry 重规划扣费。
            if (await ExpertJobTradePublication.PublishAsync(PublishCustomer, PublishOwner) is { } publicationError)
            {
                log.Write("expert_job_trade_publication_failed", new { Customer = customer.Id, Owner = owner.Id, Message = publicationError.Message });
                transport.Disconnect();
                return CommandResult.CloseConnection;
            }
            async Task PublishOwner() => await transport.Publish(results.Owner, ExpertJobPackets.Info(ownerReceipt.Character, scripts, previous: owner));
            async Task PublishCustomer()
            {
            if (job == 3 && action.Disjoint is { } products)
            {
                await context.Reply(opcode, ExpertJobPackets.DisjointResult(actor, products, receipt.Character.Inventory.Gold,
                    (uint)ownerReceipt.Character.ExpertMachineEndurance), "expert_job_trade_disjoint", Current);
                var acknowledged = customerInventory.ChangedSlots.Where(slot => slot.List == InventoryListType.Main).ToHashSet();
                foreach (var kind in customerInventory.ChangedCurrencies)
                    if (ItemSlotBoundService.CurrencySlot(kind) is { } slot) acknowledged.Add(new(InventoryListType.Main, slot));
                await inventory.RefreshAsync(context, results.Customer, acknowledgedSlots: acknowledged);
            }
            else
            {
                await inventory.RefreshAsync(context, results.Customer);
                await context.Reply(opcode, ExpertJobPackets.EnchantStoreSuccess((uint)ownerReceipt.Character.ExpertMachineEndurance,
                    (uint)scripts.Level(1, ownerReceipt.Character.ExpertJobExperience),
                    action.EnchantRarity ?? throw new InvalidDataException("附魔交易回执缺少已用稀有度"), action.Succeeded), "expert_job_trade_enchant", Current);
            }
            }
            log.Write("expert_job_trade", new { Customer = customer.Id, Owner = owner.Id, Job = job, Fee = fee,
                Succeeded = action.Succeeded, OwnerEndurance = ownerReceipt.Character.ExpertMachineEndurance,
                CustomerRevision = receipt.Character.Revision, OwnerRevision = ownerReceipt.Character.Revision });
            return CommandResult.Handled;
        }
        finally { transport.Release(); }
    }
}
