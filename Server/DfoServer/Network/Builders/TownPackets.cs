using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.GameWorld.Channels;
using DfoServer.Game.Quests;

namespace DfoServer.Network.Builders;

internal enum ActorSceneState : byte { Town = 0, Dungeon = 1 }

// Client 包体构造。只读取角色快照，不修改会话或访问数据库。
internal static partial class TownPackets
{
    public static byte[] Select(Character character, CharacterFatigueState? displayedFatigue = null)
    {
        var fatigue = displayedFatigue ?? character.Fatigue ?? CharacterFatigueState.NewOrdinary;
        fatigue.Validate();
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)1);
            writer.Write(0u);
            writer.Write((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.Write(character.Id);
            writer.Write(fatigue.Used);
            writer.Write(fatigue.Maximum);
            writer.Write(fatigue.UsedMaximum);
            writer.Write((byte)0);
            writer.Write(0u);
            for (int i = 0; i < 30; i++)
            {
                // 任务条件编码为 0 时客户端视为达成；单地图清图条件初始为 1，空槽仍为 FFFF/0。
                ushort quest = ushort.MaxValue;
                uint conditionState = 0;
                foreach (var active in character.ActiveMainQuests)
                {
                    if (active.Slot != i)
                        continue;
                    quest = active.QuestId;
                    conditionState = QuestQuestionProgress.ClientState(active.ConditionState);
                }

                writer.Write(quest);
                writer.Write(conditionState);
            }

            writer.Write(0u);
            writer.Write(new byte[16]); // 020296D0: four quest bookmarks.
            writer.Write(character.Position.Town); // 02341BCA 读初始城镇，02341E12 用它切换城镇；此处不是职业。
            bool training = character.Tutorial is { Completed: false };
            // 03B32D50逐项设置完成位；空列表保留客户端按TutorialFlow选择首个训练副本的路径。
            writer.Write((byte)0);
            writer.Write(training ? (byte)0 : (byte)1);
            if (!training) writer.Write((byte)31);
            writer.Write(fatigue.SelectionAuxiliary);
            writer.Write(fatigue.Remaining);
            writer.Write((byte)0);
            writer.Write(fatigue.ContextAuxiliary);
            writer.Write((byte)0);
            writer.Write((byte)0); // Tail quest count and premium flag.
            if (stream.Length != (training ? 234 : 235))
                throw new InvalidDataException("Client select size changed");
            return stream.ToArray();
        }
    }

    // 熟练等级与当前级经验属于角色附属owner的持久化进度，Actor 与本人 Player 是两个独立消费点：
    // Actor 直接投影尾部字段，Player 通过17字节头覆盖同一组客户端字段，两处必须取同一份值。
    static (uint Level, ulong Experience) Proficiency(Character character) =>
        (character.DungeonState?.ProficiencyLevel ?? 0u, character.DungeonState?.ProficiencyExperience ?? 0UL);

    public static byte[] Actor(Character character, DfoServer.Game.Pvp.PvpDisplayGrade pvpGrade, ChannelIdentity channel = default, long? utcSeconds = null,
        string adventureName = "", ActorSceneState sceneState = ActorSceneState.Town, bool premiumPcRoom = false)
    {
        if (character.BlacklistCount < 0)
            throw new InvalidDataException("角色被拉黑计分不能为负数");
        if (pvpGrade.PowerTitle > 9) throw new InvalidDataException("战斗力称谓超出客户端范围");
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)0);
            writer.Write((ushort)1);
            // 0267DA20按这两字节与NOTI1身份比较，决定本地角色表或跨频道缓存。
            writer.Write(channel.ServerIndex);
            writer.Write(channel.Number);
            // 0267DA80读取116B状态；偏移51为17B CP936冒险团昵称，最后一字节保留NUL。
            byte[] state = new byte[116];
            state[7] = 1;
            // 0267F6FB只接受1；与名册tail34同源，不在ACK4或Player追加未证字段。
            state[43] = character.IsPvpPrivate ? (byte)1 : (byte)0;
            // 0267F78D将状态+44h写入Data+488；023BC931据此安装真实组队栏边框。
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(68),
                InventorySkinStorageService.Merged(character.Inventory).PartyFrameSelection(
                    utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            // 0267B42E/0267B43F将头部+59h转为独立外观实体；真实宠物仍由尾部声明。
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(89),
                InventoryCreatureSkinService.AppearanceTemplateId(character, utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            CharacterName.Encoding.GetEncoder().Convert(adventureName.AsSpan(), state.AsSpan(51, 16), true,
                out _, out _, out _);
            // 0267ECF2读取头部+6Eh，经Data+49Ch恢复城镇及战斗武器幻化，独立于克隆来源。
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(110), character.Inventory.Skins.Selected);
            // Actor原始状态末尾u16为心情字段。
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(114), character.Emotion);
            writer.Write(state);
            writer.Write(character.Id);
            PacketEncoding.WriteString(writer, character.Name);
            writer.Write(character.Job);
            writer.Write(character.GrowType);
            writer.Write(character.Level);
            writer.Write(pvpGrade.Level); // 02677060：u8写入Data+30C当前决斗等级。
            writer.Write(pvpGrade.PowerTitle); // 02677110写Data+314；02D83E60非零时映射到21至29档称谓。
            // 0267DF0B读入成员DTO+1C；原生45发送门禁要求副本成员状态非零。
            writer.Write((byte)sceneState);
            EquipmentProtocolWriter.Appearance(writer, character.Inventory);
            // 0267DF38读取外观表后的u32并存入角色DTO+260h，独立于真实称号13槽。
            writer.Write(character.CloneTitleItemId);
            writer.Write(new byte[4]);
            EquipmentProtocolWriter.NameTag(writer, character.Inventory, utcSeconds);
            writer.Write((byte)0);
            writer.Write(character.Guild?.Id ?? 0u);
            writer.Write((byte)0);
            var creature = character.Inventory.Find(new(InventoryListType.Equipment, 26));
            // 0267B3B0读取模板/名称/启用标志；0267B425取反后经02A8E040写入城镇宠物禁用位。
            bool creatureEnabled = creature?.Creature is not null;
            writer.Write(creatureEnabled ? checked((uint)creature!.Core.ItemId) : 0u);
            PacketEncoding.WriteString(writer, creature?.Creature?.Name ?? "");
            writer.Write(creatureEnabled ? (byte)1 : (byte)0); // 零会令02CD6FA0跳过场景挂接与更新。
            bool blackDiamond = character.BlackDiamondExpiresAt > (utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            // 02677320写Data+33C：网吧身份控制index2及原生退图警告，不由黑钻联动开启。
            writer.Write(premiumPcRoom ? (byte)1 : (byte)0);
            // 02677430写Data+344；02AA1A60独立绘制index3，两种身份同时有效时保留两张图标。
            writer.Write(blackDiamond ? (byte)1 : (byte)0);
            // 0267E030读取u32到Data+350；02AA7BFC起按100/200/300选择GradeB/C/D，不能写装备称号ID。
            writer.Write(character.BlacklistCount);
            PacketEncoding.WriteString(writer, character.Guild?.Name ?? "");
            // 0267771A→02DAB190将此packed值写入公会图案/背景缓存；零值会覆盖已保存会标。
            writer.Write(character.Guild?.Appearance.Mark ?? 0u);
            writer.Write((ushort)0); // 026775E3 / 026775F9: mandatory fields after the name.
            writer.Write(0u);
            writer.Write((byte)1); // 0267E0A8 默认1，写DTO bool。
            writer.Write(new byte[2]); // 02679660 两个u8。
            // USERINFO Actor：026777A0 读 type:u8 + expertness:u32 进局部DTO+3B0/+3B4；0267E541 后 02D8B110→015034E0 装回活 I。
            writer.Write(character.ExpertJobType);
            writer.Write(checked((uint)character.ExpertJobExperience));
            writer.Write((byte)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ushort)0);
            writer.Write(new byte[2]);
            writer.Write((ushort)0); // 02678840.
            // 026768F0 依次读 u32 熟练等级与 u64 熟练经验，经 01A16420/01A16470 写角色
            // Data+42C/+430/+434；左下角HUD与「熟练Lv」窗口直接消费这三个字段。
            // 满级停发NOTI37后没有其他同步通道，Actor 必须投影持久化的熟练状态。
            var proficiency = Proficiency(character);
            writer.Write(proficiency.Level);
            writer.Write(proficiency.Experience);
            writer.Write(character.VisibilityFlags); // 保留原无关显示位，三项外观开关与当前角色主设置一致。
            writer.Write(0u);
            // 0267F04B覆盖Data+35C，城镇队伍HP/MP共用该百分比；外观刷新不能把已有100%清零。
            writer.Write(sceneState == ActorSceneState.Town ? (byte)100 : (byte)0);
            writer.Write((ushort)0);
            // 0267F091 读取原属大区，经 03CECD60 写 player+3FC；TAG 候选将0当作空槽。
            // 此字段独立于头部频道身份，取实际角色所在大区，不改后面的列表哨兵FF。
            writer.Write(channel.ServerIndex);
            writer.Write((ushort)0);
            writer.Write((byte)0);
            writer.Write((ushort)0);
            // 02677E60→026780B6：FF为未开通；0/1安装当前方案并设置双树资格。
            writer.Write(character.Learning.TreeWireValue);
            writer.Write(new byte[2]);
            writer.Write(0u);
            writer.Write(new byte[2]); // 0267F273..0267FED3.
            return stream.ToArray();
        }
    }

    public static byte[] Position(Character character, bool area, ushort scaledSpeed = 100)
    {
        var position = character.Position;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(character.Id);
            if (area)
            {
                writer.Write(position.Town);
                writer.Write(position.Area);
            }

            writer.Write(position.X);
            writer.Write(position.Y);
            writer.Write(position.Direction);
            if (area)
                // 02A90970将状态bit1写入转职特效显示记录，切图不能固定清零。
                writer.Write((byte)(1 | (character.VisibilityFlags & 2)));
            else
                writer.Write(scaledSpeed);
            return stream.ToArray();
        }
    }

    public static byte[] Player(Character character, CharacterStats attributes, int adventureLevel,
        ChannelIdentity channel = default, DateTimeOffset? utcNow = null)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)1);
            writer.Write((ushort)1);
            writer.Write(channel.ServerIndex);
            writer.Write(channel.Number);
            // 0267C4F0 把紧随其后的17字节头读进 62DA598，02676970 再无条件用 header+2 的u32与
            // header+6 的u64覆盖角色 Data+42C/+430/+434；本包全零会把Actor刚写入的熟练状态清成
            // 「挑战者 0」。必须在这里投影与Actor相同的持久化熟练等级/当前级经验。
            var progress = Proficiency(character);
            byte[] proficiency = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(proficiency.AsSpan(2), progress.Level);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(proficiency.AsSpan(6), progress.Experience);
            writer.Write(proficiency);
            // 0267C914/0267C924会以头部末字节重设幻化bit5，必须与后续装备栏标志一致。
            writer.Write((byte)((character.EquipmentSlotFlags & 32) == 0 ? 0 : 1));
            writer.Write(character.Id);
            writer.Write(character.Learning.Experience); // 02678520 的完整u64累计经验。
            byte[] stats = CharacterStatsPackets.Build(attributes,
                StaminaRecoveryService.Percent(character.Inventory, utcNow ?? DateTimeOffset.UtcNow));
            writer.Write(stats.Length);
            writer.Write(stats); // 03BE25C0: u32 length followed by raw bytes.
            writer.Write(character.EquipmentSlotFlags); // Full03 0267D0A9/0267D0CB：装备槽标志写本人容器+1AC。
            EquipmentProtocolWriter.Player(writer, character.Inventory);
            writer.Write(character.CloneTitleItemId);
            EquipmentProtocolWriter.Runes(writer, character.Inventory); // 023C3A90：六个符文按槽210–215排列。
            // 0267BFA0：指定技能及登记项；每项的完整物品块由同一背包条目编码器生成。
            SkillSwitchInventoryPackets.WriteUserInfo(
                writer,
                character.SkillSwitchInventory,
                character.Inventory,
                (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
            EquipmentProtocolWriter.NameTag(writer, character.Inventory);
            // 02676C60 -> 02676660: active set, two (u8 count; u16 id/u8 level) skill lists.
            writer.Write(character.Learning.TreeWireValue);
            for (int tree = 0; tree < 2; tree++)
            {
                CharacterSkill[] skills = character.Learning.Initialized
                    ? character.Learning.Trees.Single(value => value.Context == tree).Skills
                        .OrderBy(skill => skill.Id).Select(skill => new CharacterSkill(skill.Id, skill.Level)).ToArray()
                    : character.Skills;
                writer.Write(checked((byte)skills.Length));
                foreach (var skill in skills)
                {
                    writer.Write(skill.Id);
                    writer.Write(skill.Level);
                }
            }

            writer.Write((byte)0);
            writer.Write(new byte[4]); // Direct byte, then 02676300 count plus three bytes.
            writer.Write((byte)0);
            writer.Write(0u); // 02676410 / 02676510 empty lists.
            // tail[10..13]：0267C8B6读u32后交02B644B0安装本人冒险团等级，02BE4710据等级重建常驻效果；
            // 0被判为无效值并清除效果，所有本人Player刷新都必须带账号当前等级。
            writer.Write(checked((uint)adventureLevel));
            writer.Write(0u); // 02675CD0是成长胶囊计量经验，写player+0x4560，不能填冒险团等级。
            writer.Write(new byte[2]); // 0267B480及其后的独立u8。
            // 0267AC80读类别数量后按类型展开；原先把空类别的计数字节并入上面的保留字节。
            // 选择可以指向账号皮肤仓的条目，分组按角色与账号并集投影。
            SkinStoragePackets.WriteUserInfo(writer, Game.Inventory.Services.InventorySkinStorageService.Merged(character.Inventory),
                (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
            return stream.ToArray();
        }
    }

    public static byte[] CompletedQuests(Character character)
    {
        // 0/356 -> 023A0000 reads a raw 30000-byte table through 03BE25C0.
        byte[] body = new byte[30004];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, 30000);
        var done = new HashSet<int>();
        foreach (int id in character.CompletedQuests.Concat(character.AccountCompletedQuests))
        {
            if (id is <= 0 or >= 30000)
                throw new InvalidDataException("Client quest table bounds");
            body[4 + id] = 1;
            done.Add(id);
        }
        foreach (var (id,answer) in character.QuestAnswers.Concat(character.AccountQuestAnswers))
        {
            if (id is <= 0 or >= 30000 || body[4+id] == 0 || answer == byte.MaxValue)
                throw new InvalidDataException("任务回答没有对应持久完成记录");
            body[4+id] = checked((byte)(answer+1));
        }

        // S5A9 的 shonan.mm 地图30仍引用已无任务定义的13700。028CBE70要求状态3，
        // 否则028D7D12/028D8B76同时跳过图标、等级及悬浮；356在查定义前安装完成位。
        // 真实记录及回答校验后才补显示标记，不写存档、不授予奖励或入场资格。
        if (body[4 + 13700] == 0)
            body[4 + 13700] = 1;
        // 时空裂缝气球读同一张完成表的 [is clear quest]。区域深渊任务已完成后补外传显示位，
        // 不写存档；已进入的真实外传链优先于显示兼容，选图/入场资格仍见 InfernoQuestUnlock。
        InfernoQuestUnlock.ApplyDisplayBits(body, done, character.ActiveMainQuests);
        return body;
    }

    public static byte[] StoryLevel(Character character)
    {
        // 0/1382 -> 0176A230 stores StoryDigest+60; 01768FE0 compares accepted vs current level.
        byte[] body = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, character.AcceptedStoryLevel);
        return body;
    }

    public static byte[] RoomReturnTown(Location point)
    {
        // NOTI1379 -> 023D0680 reads one i32; 03B875C0 stores TownScene+3A0.
        byte[] body = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(body, point.Town);
        return body;
    }

    public static byte[] ActorState(ushort id)
    {
        // 023D6D80: u8 count, then u16 actor id and u8 state; state 0 is town.
        return new byte[]
        {
            1,
            (byte)id,
            (byte)(id >> 8),
            0
        };
    }

    public static byte[] RemoveActor(ushort id)
    {
        // Client NOTI6的023D179F只读一个u16，随后023D1929按该ID移除客户端actor。
        byte[] body = new byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body, id);
        return body;
    }

    public static byte[] HideTownEntity(ushort id) =>
        // NOTI3逐行消费：1移除城镇实体，0恢复城镇状态但不重建实体；Actor及队伍引用均保留。
        [2, (byte)id, (byte)(id >> 8), 1, (byte)id, (byte)(id >> 8), 0];

    public static byte[] Transition(Character character)
    {
        var position = character.Position;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new byte[] { position.Town, position.Area });
            writer.Write((ushort)1);
            writer.Write(character.Id);
            writer.Write(position.X);
            writer.Write(position.Y);
            writer.Write(new byte[] { position.Direction, (byte)(1 | (character.VisibilityFlags & 2)), 1 });
            return stream.ToArray(); // 023DFA82 consumes a third byte after direction and area state.
        }
    }
}
