using System.Text.RegularExpressions;
using System.Collections.Immutable;
using PvfLib;
using DfoServer.GameWorld.Items;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Characters;

internal sealed class CharacterCatalog(Dictionary<byte, CharacterProfile> profiles)
{
    private readonly Dictionary<byte, CharacterAttributeDefinition> attributeDefinitions = new();

    public CharacterProfile Get(byte job) => profiles.GetValueOrDefault(job) ?? throw new InvalidDataException("Ordinary character job is not implemented");
    public CharacterAttributeDefinition GetAttributeDefinition(byte job) => attributeDefinitions.GetValueOrDefault(job)
        ?? throw new InvalidDataException("角色属性资源未装载");

    internal static IReadOnlyDictionary<int, string> ReadJobList(PvfArchive pvf)
    {
        PvfToken[] tokens = pvf.ReadTokens("character/character.lst");
        if (tokens.Length == 0 || tokens.Length % 2 != 0)
            throw new InvalidDataException("角色名册为空或ID/路径不成对");
        var jobs = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Length; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i].Value is < 0 or > 15 || tokens[i + 1].Type != 6)
                throw new InvalidDataException("角色名册编号或路径类型无效");
            string relative = pvf.ResolveString(tokens[i + 1].Value);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains(':') ||
                relative.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
                throw new InvalidDataException("角色名册路径非法");
            string path = PvfArchive.NormalizePath(relative);
            if (pvf.FindFile("character/" + path)?.DataType != 1 || !jobs.TryAdd(tokens[i].Value, path))
                throw new InvalidDataException("角色名册重复ID或CHR资源缺失");
        }
        return jobs;
    }

    public static CharacterCatalog Load(PvfArchive pvf, ItemCatalog itemCatalog)
    {

        var list = ReadJobList(pvf);
        var profiles = new Dictionary<byte, CharacterProfile>();
        var catalog = new CharacterCatalog(profiles);
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
            var profile = new CharacterProfile(job, stats, skills)
            {
                Equipment = starter.OrderBy(item => item.Slot).ToArray(),
                DefaultAvatarEquipment = ReadDefaultAvatars(pvf, itemCatalog, path),
                GrowthOptions = ParseGrowthOptions(text, job)
            };
            profile.Validate();
            profiles.Add(job, profile);
            // 资源块编号与实际 GrowType 的消费映射由属性规则解释，不在装载时猜测。
            catalog.attributeDefinitions.Add(job, CharacterAttributeDefinition.Parse("character/" + path,
                pvf.ReadTokens("character/" + path), pvf.ResolveString));
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

        return catalog;
    }

    static ImmutableDictionary<byte, int> ReadDefaultAvatars(PvfArchive pvf, ItemCatalog items, string characterPath)
    {
        string path = "character/characteretc/" + Path.GetFileNameWithoutExtension(characterPath) + ".etc";
        if (!pvf.Contains(path)) path = "character/characteretc/default.etc";
        if (!pvf.Contains(path)) return ImmutableDictionary<byte, int>.Empty;
        var result = ImmutableDictionary.CreateBuilder<byte, int>();
        foreach (int id in CharacterEquipmentFile.ReadDefaultEquipment(pvf.ReadTokens(path), pvf.ResolveString))
        {
            if (id <= 0 || !items.ContainsEquipment(id)) continue;
            var template = items.Get(id);
            if (template.Kind == ItemKind.Avatar && template.EquipmentSlot <= 10)
                result[template.EquipmentSlot] = id;
            else if (template.Kind == ItemKind.Equipment && template.EquipmentSlot == 12)
                result[10] = id;
        }
        return result.ToImmutable();
    }

    internal static CharacterGrowthOptions ParseGrowthOptions(string text, byte job)
    {
        string[] names = ReadNames(Section(text, "growtype name"));
        var stages = Regex.Matches(text, @"\[growtype (\d+)\]");
        var options = new List<CharacterGrowthOption>();
        for (int i = 0; i < stages.Count; i++)
        {
            int first = int.Parse(stages[i].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
            if (first < 0 || first >= names.Length || names[first].Length == 0 || names[first].StartsWith("//", StringComparison.Ordinal))
                continue;
            string stage = text[stages[i].Index..(i + 1 < stages.Count ? stages[i + 1].Index : text.Length)];
            string[] awakeningNames = ReadNames(Section(stage, "awakening name")).Take(2).ToArray();
            // 普通未转职不显示觉醒；黑暗武士和缔造者的成长段使用first=0。
            if (first == 0 && job is not (9 or 10)) awakeningNames = [];
            options.Add(new(checked((byte)first), names[first], Array.AsReadOnly(awakeningNames)));
        }
        return new(names.FirstOrDefault() ?? "", options.AsReadOnly());
        static string[] ReadNames(string value) => Regex.Matches(value, "`([^`]*)`").Select(match => match.Groups[1].Value).ToArray();
    }
}
