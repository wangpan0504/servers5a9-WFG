using System.Runtime.CompilerServices;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.EpicProduction;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class EpicProductionHandler(EpicProductionCatalog production,EpicProductionAbilityCatalog abilities,EpicProductionCompatibility compatibility,
    ItemCatalog items,GoldLimitCatalog gold,EpicPieceCatalog pieces,EpicProductionProjection projection,
    InventoryProjection inventory,TimeProvider? clock=null) : IGameCommandModule
{
    readonly TimeProvider timer=clock??TimeProvider.System;
    // 每个连接仅保留当前包的一份计划/提交回执，连接回收时自动释放；不是并行角色状态。
    readonly ConditionalWeakTable<GameConnectionContext,Pending> retries=new();
    sealed class Pending
    {
        public Packet? Request;
        public bool Planned;
        public EpicProductionPlan? Plan;
        public EpicProductionCommit? Committed;
    }
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.EPIC_PRODUCTION_START_FINISH,Execute,nameof(EpicProductionHandler));
        commands.Register(CmdPacketType.EPIC_PRODUCTION_PROCESS,Execute,nameof(EpicProductionHandler));
        commands.Register(CmdPacketType.EPIC_PRODUCTION_MATERIAL_COMPOUND,Execute,nameof(EpicProductionHandler));
        commands.Register(CmdPacketType.EPIC_PRODUCTION_CHANGE_ITEM,Execute,nameof(EpicProductionHandler));
        commands.Register(CmdPacketType.EPIC_PRODUCTION_ABILITY_OPTION,Execute,nameof(EpicProductionHandler));
    }
    async Task<CommandResult> Execute(GameConnectionContext context,Packet packet)
    {
        var pending=retries.GetOrCreateValue(context);
        if (!ReferenceEquals(pending.Request,packet))
        {
            pending.Request=packet;pending.Planned=false;pending.Plan=null;pending.Committed=null;
        }
        try
        {
            if (pending.Committed is null && !context.BlocksTownActions)
                pending.Committed=context.Session.PlanAndCommitEpicProduction(character=>
                {
                    if (!pending.Planned)
                    {
                        pending.Planned=true;pending.Plan=Plan(character,packet);
                    }
                    // 同一包忙锁重试保留随机结果与原角色快照；版本变化时Session拒绝，绝不重抽。
                    return pending.Plan;
                });
        }
        catch (InvalidDataException)
        {
            pending.Planned=true;pending.Plan=null;
        }
        if (pending.Committed is not { } receipt)
        {
            await context.Reply((CmdPacketType)packet.Opcode,EpicProductionPackets.Rejected(3),"epic_production_rejected");
            return CommandResult.Handled;
        }
        bool CanSend()=>context.Session.CanPublishEpicProduction(receipt);
        await inventory.RefreshAsync(context,receipt.Inventory);
        var plan=receipt.Plan;
        byte[] body=packet.Opcode switch
        {
            (ushort)CmdPacketType.EPIC_PRODUCTION_START_FINISH=>EpicProductionPackets.StartedOrFinished(plan.After.WeaponId,plan.After.Charge),
            (ushort)CmdPacketType.EPIC_PRODUCTION_ABILITY_OPTION=>[1],
            (ushort)CmdPacketType.EPIC_PRODUCTION_CHANGE_ITEM=>EpicProductionPackets.Changed(plan.After.WeaponId),
            (ushort)CmdPacketType.EPIC_PRODUCTION_PROCESS=>EpicProductionPackets.Processed(plan.ProducedWeapon?production.MaximumCharge:plan.After.Charge,plan.After.WeeklyUses,plan.BigChance),
            _=>EpicProductionPackets.Crafted(plan.CraftedCount,production.Liquids[plan.CraftedLiquidId].Group,
                production.Liquids[plan.CraftedLiquidId].Group switch {1=>plan.After.IntermediateMaterialRemainder,2=>plan.After.OrdinaryMaterialRemainder,_=>0})
        };
        await context.Reply((CmdPacketType)packet.Opcode,body,"epic_production_committed",CanSend);
        if (plan.ProducedWeapon)
            await context.Reply(CmdPacketType.EPIC_PRODUCTION_START_FINISH,
                EpicProductionPackets.StartedOrFinished(plan.Before.WeaponId,production.MaximumCharge),"epic_production_finished",CanSend);
        await projection.RefreshAsync(context,receipt);
        return CommandResult.Handled;
    }
    EpicProductionPlan? Plan(Character character,Packet packet)
    {
        var now=timer.GetUtcNow();int week=WeeklyDungeonPolicy.WeekKey(now);
        switch ((CmdPacketType)packet.Opcode)
        {
            case CmdPacketType.EPIC_PRODUCTION_START_FINISH:
                return EpicProductionPlan.Start(character,character.EpicProduction,EpicProductionRequests.Start(packet.Body),week,production,items,gold,pieces,now);
            case CmdPacketType.EPIC_PRODUCTION_ABILITY_OPTION:
                var ability=EpicProductionRequests.Ability(packet.Body);
                return EpicProductionPlan.Ability(character,ability.Slot,ability.Group,ability.Option,production,abilities,pieces,now);
            case CmdPacketType.EPIC_PRODUCTION_CHANGE_ITEM:
                var change=EpicProductionRequests.Change(packet.Body);
                return EpicProductionPlan.Change(character,change.WeaponId,change.Materials,production,pieces,now);
            case CmdPacketType.EPIC_PRODUCTION_PROCESS:
                var rows=EpicProductionRequests.Process(packet.Body);
                bool great=compatibility.DrawGreatSuccess(Random.Shared.Next);
                return EpicProductionPlan.Process(character,rows,week,great,production,items,gold,pieces,now);
            case CmdPacketType.EPIC_PRODUCTION_MATERIAL_COMPOUND:
                var request=EpicProductionRequests.Craft(packet.Body);
                if (!production.Liquids.TryGetValue(request.LiquidId,out var liquid)) return null;
                int[] rolls=liquid.MaximumOutput>liquid.MinimumOutput
                    ?request.Materials.Select(_=>Random.Shared.Next(production.OutputMaximumRate)).ToArray():[];
                return EpicProductionPlan.Craft(character,request.LiquidId,request.Materials,rolls,production,items,gold,pieces,now);
            default:return null;
        }
    }
}
