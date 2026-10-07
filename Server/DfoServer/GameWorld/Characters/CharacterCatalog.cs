using System.Text.RegularExpressions;
using PvfLib;
using DfoServer.GameWorld.Items;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Characters;

internal sealed class CharacterCatalog(Dictionary<byte, CharacterProfile> profiles)
{
    public CharacterProfile Get(byte job) => profiles.GetValueOrDefault(job) ?? throw new InvalidDataException("Ordinary character job is not implemented");
    public static CharacterCatalog Load(PvfArchive pvf, ItemCatalog itemCatalog)
    {

        var list = List(pvf.ReadText("character/character.lst"));
        if (!list.Keys.Order().SequenceEqual(Enumerable.Range(0, 16)))
            throw new InvalidDataException("Character job list changed");
        var profiles = new Dictionary<byte, CharacterProfile>();
        foreach (var entry in list.OrderBy(p => p.Key))
        {
            byte job = checked((byte)entry.Key);
            string path = entry.Value;
            string text = pvf.ReadText("character/" + path);
            if (Section(text, "job").Length == 0)
                throw new InvalidDataException("Character job identity missing");
            int start = text.IndexOf("[initial value]", StringComparison.Ordinal), end = text.IndexOf("[growtype 1]", StringComparison.Ordinal);
            if (start < 0 || end <= start)
                throw new InvalidDataException("Character initial value block missing");
            string initial = text[start..end];
            var stats = new CharacterStats(
                MaxHp: checked((uint)Scaled("HP MAX")),
                MaxMp: checked((uint)Scaled("MP MAX")),
                PhysicalAttack: checked((short)Scaled("physical attack")),
                PhysicalDefense: checked((short)Scaled("physical defense")),
                MagicalAttack: checked((short)Scaled("magical attack")),
                MagicalDefense: checked((short)Scaled("magical defense")),
                FireResistance: checked((short)Scaled("fire resistance", true)),
                WaterResistance: checked((short)Scaled("water resistance", true)),
                DarkResistance: checked((short)Scaled("dark resistance", true)),
                LightResistance: checked((short)Scaled("light resistance", true)),
                InventoryLimit: checked((uint)Scaled("inventory limit")),
                MpRecovery: checked((ushort)Scaled("MP regen speed")),
                MoveSpeed: checked((uint)Scaled("move speed")),
                AttackSpeed: checked((ushort)Scaled("attack speed")),
                CastSpeed: checked((ushort)Scaled("cast speed")),
                HitRecovery: checked((ushort)Scaled("hit recovery")),
                JumpPower: checked((ushort)Scaled("jump power")),
                Weight: checked((uint)Scaled("weight")));
            // job0–15 都使用 CHR [initial value] 里的 [skill] 对。同 ID 跨职业不合并。
            int[] pairs = Integers(Block(initial, "skill"));
            if (pairs.Length == 0 || pairs.Length % 2 != 0)
                throw new InvalidDataException("Character starter skills missing");
            CharacterSkill[] skills = Enumerable.Range(0, pairs.Length / 2).Select(i => new CharacterSkill(checked((ushort)pairs[i * 2]), checked((byte)pairs[i * 2 + 1]))).OrderBy(s => s.Id).ToArray();

            string createEquipment = Block(text, "create equipment list").Replace("`", "", StringComparison.Ordinal);
            var starter = new List<StarterEquipment>();
            foreach (Match match in Regex.Matches(createEquipment, @"\[(weapon|coat|pants|waist|shoes)\]\s*(\d+)", RegexOptions.IgnoreCase))
            {
                byte slot = ItemCatalog.Slot(match.Groups[1].Value);
                var template = itemCatalog.Get(int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                if (template.EquipmentSlot != slot || template.MinimumLevel != 1 || (template.Jobs & (1 << job)) == 0 ||
                    template.RequiredSkill != 0 && !skills.Any(skill => skill.Id == template.RequiredSkill))
                    throw new InvalidDataException("初始装备与职业定义不一致");
                starter.Add(new(slot, template));
            }

            if (starter.Count != 5 || starter.Select(item => item.Slot).Distinct().Count() != 5)
                throw new InvalidDataException("普通初始装备列表不完整");
            var profile = new CharacterProfile(job, stats, skills) { Equipment = starter.OrderBy(item => item.Slot).ToArray() };
            profile.Validate();
            profiles.Add(job, profile);
            int Scaled(string tag, bool optional = false)
            {
                string value = Section(initial, tag);
                if (value.Length == 0 && optional)
                    return 0;
                var match = Regex.Match(value, @"^[-+]?\d+(?:\.\d+)?\s*$");
                if (!match.Success)
                    throw new InvalidDataException("Missing or invalid initial character field: " + tag);
                decimal scaled = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) * 10;
                if (scaled != decimal.Truncate(scaled))
                    throw new InvalidDataException("Character field exceeds wire precision: " + tag);
                return checked((int)scaled);
            }
        }

        return new CharacterCatalog(profiles);
    }
}
