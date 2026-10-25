using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Pvp;

internal static class PvpPrivateCharacterPlan
{
    public static NewCharacter Prepare(string name,CharacterProfile profile,PvpPrivateCharacterDefinition definition,
        int accountLevel,Location location,SkillLearningCatalog skills,CharacterGrowthCatalog growth,ItemCatalog items,DateTimeOffset now)
    {
        if(profile.Job!=definition.Job || !profile.GrowthOptions.GrowTypes.Any(row=>row.Value==definition.GrowType) ||
            definition.RecreatePenalty is not int days || definition.Equipment.IsEmpty)
            throw new NotSupportedException("专用建角分支或原重建惩罚声明不完整");
        var creation=CharacterCreation.Prepare(name,profile,location,location,skills.Get);
        var learning=SkillPointLedger.Synchronize(profile.Job,definition.GrowType,85,creation.InitialLearning with {Experience=growth.GetLevelStartExperience(85)},skills);
        var result=ImmutableArray.CreateBuilder<InventoryItem>();
        short next=ItemSlotBoundService.Range(ItemKind.Equipment,0).Start;
        var occupied=new HashSet<byte>();var templates=new HashSet<int>();
        byte equipmentFlags=0;
        foreach(var row in definition.Equipment)
        {
            var template=items.Get(row.ItemId);
            byte slot=ItemCatalog.Slot(row.Slot.Trim('[',']'));
            if(row.Flag is not (0 or 1) || template.Kind!=ItemKind.Equipment || !template.PvpFreeEquipment || template.EquipmentSlot!=slot ||
                !template.AllowsUse(85,profile.Job) || !templates.Add(row.ItemId) ||
                template.RequiredSkill!=0 && !learning.Trees.Single(tree=>tree.Context==0).Skills.Any(skill=>skill.Id==template.RequiredSkill))
                throw new NotSupportedException("专用建角装备原声明、职业或部位不符");
            InventorySlot position;
            // 严格资源一致性兼容：1是唯一穿戴，0是备用装备；客户端third字段消费尚未证实。
            if(row.Flag==1)
            {
                if(!occupied.Add(slot)) throw new InvalidDataException("专用建角同部位有多件穿戴装备");
                position=new(InventoryListType.Equipment,slot);
                if(slot==22) equipmentFlags|=InventoryMoveService.SupportSlotFlag;
                if(slot==23) equipmentFlags|=InventoryMoveService.MagicStoneSlotFlag;
                if(slot==25) equipmentFlags|=InventoryMoveService.EarringSlotFlag;
            }
            else
            {
                if(next>ItemSlotBoundService.Range(ItemKind.Equipment,0).End) throw new InvalidDataException("专用初始装备超出原背包容量");
                position=new(InventoryListType.Main,next++);
            }
            result.Add(new(0,position,InventoryRewardGrantService.Create(template,utcNow:now)));
        }
        if(!occupied.Contains(12)) throw new InvalidDataException("专用角色原资源没有唯一初始武器");
        var character=creation with {GrowType=definition.GrowType,Level=85,AcceptedStoryLevel=85,
            InitialLearning=learning,Skills=learning.Trees.Single(tree=>tree.Context==0).Skills.Select(skill=>new CharacterSkill(skill.Id,skill.Level)).ToArray(),
            EquipmentSlotFlags=equipmentFlags,Inventory=new(result.ToImmutable()),
            PvpPrivateCreation=new(accountLevel,days,now.ToUnixTimeSeconds())};
        character.Validate();return character;
    }
}
