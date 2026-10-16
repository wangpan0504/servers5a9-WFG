using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.GameWorld.Channels;
using DfoServer.Game.Quests;

namespace DfoServer.Network.Builders;

internal enum ActorSceneState : byte { Town = 0, Dungeon = 1 }

// Client 包体构造。只读取角色快照，不修改会话或访问数据库。
internal static class TownPackets
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

    public static byte[] Actor(Character character, ChannelIdentity channel = default, long? utcSeconds = null,
        string adventureName = "", ActorSceneState sceneState = ActorSceneState.Town)
    {
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
            // 0267B42E/0267B43F将头部+59h转为独立外观实体；真实宠物仍由尾部声明。
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(89),
                InventoryCreatureSkinService.AppearanceTemplateId(character, utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            CharacterName.Encoding.GetEncoder().Convert(adventureName.AsSpan(), state.AsSpan(51, 16), true,
                out _, out _, out _);
            // Actor原始状态末尾u16为心情字段。
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(state.AsSpan(114), character.Emotion);
            writer.Write(state);
            writer.Write(character.Id);
            PacketEncoding.WriteString(writer, character.Name);
            writer.Write(character.Job);
            writer.Write(character.GrowType);
            writer.Write(character.Level);
            writer.Write(new byte[2]); // Level后的两个辅助字段。
            // 0267DF0B读入成员DTO+1C；原生45发送门禁要求副本成员状态非零。
            writer.Write((byte)sceneState);
            EquipmentProtocolWriter.Appearance(writer, character.Inventory);
            writer.Write(0u);
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
            writer.Write((byte)0); // 02677320：PC房效果，与黑钻分开。
            // 02677430写Actor+344h；02AA85A4/02AA8ACC据此绘制金色名称和pcroompremiumreward.img的index3。
            writer.Write(character.BlackDiamondExpiresAt > (utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ? (byte)1 : (byte)0);
            writer.Write(0u);
            PacketEncoding.WriteString(writer, character.Guild?.Name ?? "");
            writer.Write(0u);
            writer.Write((ushort)0); // 026775E3 / 026775F9: mandatory fields after the name.
            writer.Write(0u);
            writer.Write((byte)1);
            writer.Write(new byte[2]); // Main flag, 02679660 pair.
            writer.Write((byte)0);
            writer.Write(0u); // 026777A0.
            writer.Write((byte)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ushort)0);
            writer.Write(new byte[2]);
            writer.Write((ushort)0); // 02678840.
            writer.Write(0u);
            writer.Write(0UL); // 026768F0.
            writer.Write(character.VisibilityFlags); // 保留原无关显示位，三项外观开关与当前角色主设置一致。
            writer.Write(0u);
            writer.Write((byte)0);
            writer.Write((ushort)0);
            writer.Write((byte)0);
            writer.Write((ushort)0);
            writer.Write((byte)0);
            writer.Write((ushort)0);
            writer.Write((byte)0xff);
            writer.Write(new byte[2]);
            writer.Write(0u);
            writer.Write(new byte[2]); // 0267F273..0267FED3.
            return stream.ToArray();
        }
    }

    public static byte[] Position(Character character, bool area)
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
                writer.Write((ushort)100);
            return stream.ToArray();
        }
    }

    public static byte[] Player(Character character, CharacterStats attributes, ChannelIdentity channel = default,
        DateTimeOffset? utcNow = null)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)1);
            writer.Write((ushort)1);
            writer.Write(channel.ServerIndex);
            writer.Write(channel.Number);
            writer.Write(new byte[16]);
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
            writer.Write(0u);
            writer.Write((byte)0); // 023C3A90: equipment count, scalar, extra list count.
            writer.Write((ushort)0);
            writer.Write((byte)0); // 0267BFA0: empty auxiliary inventory.
            EquipmentProtocolWriter.NameTag(writer, character.Inventory);
            // 02676C60 -> 02676660: active set, two (u8 count; u16 id/u8 level) skill lists.
            writer.Write(character.Skills.Length == 0 ? (byte)0xff : (byte)0);
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
            writer.Write(0u);
            writer.Write(0u); // Direct scalar, then 02675CD0.
            writer.Write(new byte[3]); // 0267B480, direct byte, 0267AC80 empty list.
            return stream.ToArray();
        }
    }

    public static byte[] CompletedQuests(Character character)
    {
        // 0/356 -> 023A0000 reads a raw 30000-byte table through 03BE25C0.
        byte[] body = new byte[30004];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, 30000);
        foreach (int id in character.CompletedQuests.Concat(character.AccountCompletedQuests))
        {
            if (id is <= 0 or >= 30000)
                throw new InvalidDataException("Client quest table bounds");
            body[4 + id] = 1;
        }
        foreach (var (id,answer) in character.QuestAnswers.Concat(character.AccountQuestAnswers))
        {
            if (id is <= 0 or >= 30000 || body[4+id] == 0 || answer == byte.MaxValue)
                throw new InvalidDataException("任务回答没有对应持久完成记录");
            body[4+id] = checked((byte)(answer+1));
        }

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
