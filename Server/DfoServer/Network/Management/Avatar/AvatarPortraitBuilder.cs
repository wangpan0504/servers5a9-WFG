using System.Globalization;
using System.Text.RegularExpressions;
using DfoServer.Network.Management.ImagePack;

namespace DfoServer.Network.Management.Avatar;

internal sealed record AvatarWorn(int Slot, int ItemId, int ClearAvatarId);
internal sealed record AvatarLookInput(string Name, byte Job, byte GrowType, IReadOnlyList<AvatarWorn> Equipment, int WeaponSkin);

// 一层：来源部位与物品、PVF 层名与 z 序、解析出的 IMG 路径；Drawn=false 时 Note 写原因（资源缺失、层名无法解析等）。
internal sealed record AvatarLayer(int Slot, int ItemId, string Source, string Layer, int Z, string Img, int Frame, bool Drawn, string Note, int Palette = 0);
internal sealed record AvatarPortrait(byte[]? Png, int Width, int Height, int Frame, string JobTag, IReadOnlyList<AvatarLayer> Layers, IReadOnlyList<string> Notes);

internal interface IAvatarImages
{
    bool TryReadFrame(string imgPath, int frame, int palette, out ImgFrame? result);
    // v6 调色板个数（v4 为 1，v2 为 0）；读不到 IMG 返回 -1。
    int PaletteCount(string imgPath);
}

// 纸娃娃合成（只读、不改库）。全部规则来自 PVF 与客户端 ImagePacks2：
// - 职业：character/character.lst 的职业编号 → .chr；[job] 是装扮脚本 [animation job] 的匹配值，[body image path] 给出皮肤路径与文件前缀，
//   [waiting motion] 第一项（站立动画）.ani 的第一帧 [IMAGE] 编号即合成帧。
// - 装扮：[animation job] 块内 [variation] a b，[layer variation] z `部位_后缀` → <皮肤目录的 Avatar 层>/<部位>/<前缀><部位><编号><后缀>.img。
//   编号先取 a×100+b（至少 4 位）；该文件不存在且 b>0 时取 a×100 号文件的第 b 个调色板（v6）。
//   武器层 `[职业前缀_]核心后缀` → <Weapon 层>/<核心>/[前缀]<核心><编号><后缀>.img；无职业前缀时先试不带前缀、再试皮肤前缀（鬼剑 sswd0000c、魔法师 mg_rod0000c）。
// - 默认外观：character/characteretc/<chr 名>.etc 的 defaultEquipment（两种写法）；按各物品自己的 [equipment type] 归位，空位才用默认。
// - 克隆装扮 ClearAvatarId>0 时画克隆外观；武器优先级：武器幻化 > 武器装扮(10) > 武器(12) > 默认武器。
// 已知缺口见 Docs/开发/GM.md：皮肤层 z 序未从资源证明，暂按 BodyZ；帽子遮发等隐藏规则、觉醒默认外观、v5 IMG 未处理。
internal sealed class AvatarPortraitBuilder(Func<string, string?> readText, Func<int, string?> equipmentPath, IAvatarImages images, Action<string>? log = null)
{
    internal const int BodyZ = 1000;
    const int Padding = 4;
    static readonly Regex Token = new(@"`(?<s>[^`]*)`|\[(?<t>[^\]\r\n]+)\]|(?<n>-?\d+)", RegexOptions.Compiled);
    static readonly string[] SlotTags =
        ["hat avatar", "hair avatar", "face avatar", "coat avatar", "pants avatar", "shoes avatar", "breast avatar", "waist avatar", "skin avatar", "aurora avatar", "weapon avatar"];

    internal readonly record struct Tok(char Kind, string Text, int Number);
    internal sealed record JobProfile(string ChrPath, string JobTag, string BodyPath, string AvatarDir, string WeaponDir, string Prefix, int Frame, IReadOnlyList<int> Defaults);
    internal sealed record ItemLook(int Slot, string Tag, int Major, int Minor, IReadOnlyList<(int Z, string Layer)> Layers, bool HasJob)
    {
        public int Number => Major * 100 + Minor;
    }

    internal static List<Tok> Tokenize(string text)
    {
        var tokens = new List<Tok>();
        foreach (Match match in Token.Matches(text))
        {
            if (match.Groups["s"].Success) tokens.Add(new('s', match.Groups["s"].Value.Trim(), 0));
            else if (match.Groups["t"].Success) tokens.Add(new('t', match.Groups["t"].Value.Trim().ToLowerInvariant(), 0));
            else if (int.TryParse(match.Groups["n"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number)) tokens.Add(new('n', match.Value, number));
        }
        return tokens;
    }

    string? Read(string path)
    {
        try { return readText(path); }
        catch (Exception ex) when (ex is FileNotFoundException or KeyNotFoundException or InvalidDataException or IOException) { return null; }
    }

    public JobProfile? Profile(byte job, List<string> notes)
    {
        string? list = Read("character/character.lst");
        if (list is null) { notes.Add("PVF 缺少 character/character.lst"); return null; }
        string? chr = null;
        var tokens = Tokenize(list);
        for (int i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i].Kind == 'n' && tokens[i].Number == job && tokens[i + 1].Kind == 's') { chr = "character/" + tokens[i + 1].Text.Replace('\\', '/').ToLowerInvariant(); break; }
        if (chr is null) { notes.Add("character.lst 没有职业 " + job); return null; }
        string? text = Read(chr);
        if (text is null) { notes.Add("PVF 缺少 " + chr); return null; }
        var chrTokens = Tokenize(text);
        string? jobTag = After(chrTokens, "job"), body = After(chrTokens, "body image path"), motion = After(chrTokens, "waiting motion");
        if (jobTag is null || body is null) { notes.Add(chr + " 缺少 [job] 或 [body image path]"); return null; }
        body = body.Replace('\\', '/');
        int skin = body.IndexOf("/skin/", StringComparison.OrdinalIgnoreCase);
        string file = body[(body.LastIndexOf('/') + 1)..];
        int bodyAt = file.IndexOf("body", StringComparison.OrdinalIgnoreCase);
        if (skin < 0 || bodyAt < 0 || !body.Contains("%04d", StringComparison.Ordinal)) { notes.Add(chr + " 的皮肤路径不是 …/skin/<前缀>body%04d.img：" + body); return null; }
        string avatarDir = body[..(skin + 1)];
        int avatarAt = avatarDir.LastIndexOf("/avatar/", StringComparison.OrdinalIgnoreCase);
        string weaponDir = avatarAt >= 0 ? avatarDir[..avatarAt] + "/Weapon/" : "";
        string chrDir = chr[..(chr.LastIndexOf('/') + 1)];
        int frame = 0;
        if (motion is not null && Read(chrDir + motion.Replace('\\', '/').ToLowerInvariant()) is { } ani)
        {
            var aniTokens = Tokenize(ani);
            int image = aniTokens.FindIndex(t => t is { Kind: 't', Text: "image" });
            if (image >= 0 && image + 2 < aniTokens.Count && aniTokens[image + 2].Kind == 'n') frame = aniTokens[image + 2].Number;
            else notes.Add("站立动画没有 [IMAGE]，使用第 0 帧");
        }
        else notes.Add("找不到站立动画，使用第 0 帧");
        string chrName = Path.GetFileNameWithoutExtension(chr);
        var defaults = new List<int>();
        if (Read("character/characteretc/" + chrName + ".etc") is { } etc) defaults.AddRange(DefaultEquipment(Tokenize(etc)));
        else notes.Add("PVF 没有 character/characteretc/" + chrName + ".etc，无默认外观（空部位不画）");
        return new(chr, jobTag, body, avatarDir, weaponDir, file[..bodyAt], frame, defaults);
    }

    static string? After(List<Tok> tokens, string tag)
    {
        int at = tokens.FindIndex(t => t.Kind == 't' && t.Text == tag);
        return at >= 0 && at + 1 < tokens.Count && tokens[at + 1].Kind == 's' ? tokens[at + 1].Text : null;
    }

    // [equipment index] `defaultEquipment` v0 v1 … 按位置；[key value index] `defaultEquipment` 首值后接 (下标, 值) 对。
    // 两种写法只取正编号，具体部位由物品自己的 [equipment type] 决定，不依赖下标含义。
    internal static IEnumerable<int> DefaultEquipment(List<Tok> tokens)
    {
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != 't' || tokens[i].Text is not ("equipment index" or "key value index") ||
                tokens[i + 1] is not { Kind: 's', Text: "defaultEquipment" }) continue;
            bool pairs = tokens[i].Text == "key value index";
            var numbers = tokens.Skip(i + 2).TakeWhile(t => t.Kind == 'n').Select(t => t.Number).ToList();
            var values = pairs ? numbers.Skip(1).Where((_, k) => k % 2 == 1) : numbers;
            return values.Where(v => v > 0).ToList();
        }
        return [];
    }

    // 读装扮/武器脚本：[equipment type] 与本职业 [animation job] 块（import script 子脚本未声明时沿父脚本找）。
    internal ItemLook? Look(int itemId, string jobTag)
    {
        string? path = equipmentPath(itemId);
        for (int depth = 0; path is not null && depth < 4; depth++)
        {
            string? text = Read(path);
            if (text is null) return null;
            var tokens = Tokenize(text);
            if (!tokens.Any(t => t is { Kind: 't', Text: "animation job" }))
            {
                string? parent = After(tokens, "import script");
                path = parent is null ? null : "equipment/" + parent.Replace('\\', '/').ToLowerInvariant();
                if (path is null) return ParseLook(tokens, jobTag);
                continue;
            }
            return ParseLook(tokens, jobTag);
        }
        return null;
    }

    internal static ItemLook ParseLook(List<Tok> tokens, string jobTag)
    {
        string tag = (After(tokens, "equipment type") ?? "").Trim('[', ']').ToLowerInvariant();
        int slot = tag == "weapon" ? 12 : Array.IndexOf(SlotTags, tag);
        if (tag is "aura skin avatar" or "aurora illusion avatar" or "aurora skin avatar" or "aurora change avatar") slot = 11;
        var layers = new List<(int, string)>();
        int major = 0, minor = 0;
        bool inJob = false, hasJob = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != 't') continue;
            if (token.Text == "animation job")
            {
                inJob = i + 1 < tokens.Count && tokens[i + 1].Kind == 's' && string.Equals(tokens[i + 1].Text, jobTag, StringComparison.OrdinalIgnoreCase);
                hasJob |= inJob;
                continue;
            }
            if (!inJob) continue;
            if (token.Text == "variation")
            {
                major = i + 1 < tokens.Count && tokens[i + 1].Kind == 'n' ? tokens[i + 1].Number : 0;
                minor = i + 2 < tokens.Count && tokens[i + 2].Kind == 'n' ? tokens[i + 2].Number : 0;
            }
            else if (token.Text == "layer variation" && i + 2 < tokens.Count && tokens[i + 1].Kind == 'n' && tokens[i + 2].Kind == 's')
                layers.Add((tokens[i + 1].Number, tokens[i + 2].Text));
        }
        return new(slot, tag, major, minor, layers, hasJob);
    }

    // 层名 → 候选 IMG 路径（按顺序取第一个存在的）。装扮 `部位_后缀`；武器 `[职业前缀_]核心+单字母后缀`
    // （有职业前缀时换成皮肤文件前缀，如 at_sswda → sg_sswd0000a；没有时先不带前缀，再带皮肤前缀）。层名不合规则返回空。
    internal static IReadOnlyList<string> ImgCandidates(JobProfile profile, bool weapon, string layer, int number)
    {
        string digits = number.ToString("D4", CultureInfo.InvariantCulture);
        if (!weapon)
        {
            int cut = layer.LastIndexOf('_');
            if (cut <= 0 || cut == layer.Length - 1) return [];
            string part = layer[..cut], suffix = layer[(cut + 1)..];
            return [profile.AvatarDir + part + "/" + profile.Prefix + part + digits + suffix + ".img"];
        }
        if (profile.WeaponDir.Length == 0) return [];
        int prefixCut = layer.IndexOf('_');
        string rest = prefixCut >= 0 ? layer[(prefixCut + 1)..] : layer;
        if (rest.Length < 2 || rest.Contains('_')) return [];
        string core = rest[..^1], dir = profile.WeaponDir + core + "/", tail = core + digits + rest[^1] + ".img";
        return prefixCut >= 0 ? [dir + profile.Prefix + tail] : [dir + tail, dir + profile.Prefix + tail];
    }

    // 先找 a×100+b 号文件（调色板 0）；没有且 b>0 时找 a×100 号文件、调色板 b（须是调色板数大于 b 的 v6）。
    internal (string Img, int Palette)? Resolve(JobProfile profile, bool weapon, string layer, ItemLook look)
    {
        foreach (string img in ImgCandidates(profile, weapon, layer, look.Number))
            if (images.PaletteCount(img) >= 0) return (img, 0);
        if (look.Minor > 0)
            foreach (string img in ImgCandidates(profile, weapon, layer, look.Major * 100))
                if (images.PaletteCount(img) > look.Minor) return (img, look.Minor);
        return null;
    }

    public AvatarPortrait Build(AvatarLookInput input, IReadOnlyList<int> tryOn)
    {
        var notes = new List<string>();
        var layers = new List<AvatarLayer>();
        var profile = Profile(input.Job, notes);
        if (profile is null) return new(null, 0, 0, 0, "", layers, notes);

        // 每个部位最终画哪件：实穿（克隆取外观编号）→ 试穿覆盖 → 默认补空位。
        var chosen = new Dictionary<int, (int Id, string Source)>();
        foreach (var worn in input.Equipment)
        {
            if (worn.Slot is < 0 or > 12 || worn.ItemId <= 0) continue;
            chosen[worn.Slot] = worn.ClearAvatarId > 0 ? (worn.ClearAvatarId, "clone") : (worn.ItemId, "worn");
        }
        var looks = new Dictionary<int, ItemLook?>();
        ItemLook? LookOf(int id) => looks.TryGetValue(id, out var look) ? look : looks[id] = Look(id, profile.JobTag);
        foreach (int id in tryOn)
        {
            var look = LookOf(id);
            if (look is null || look.Slot is < 0 or > 12) { notes.Add("试穿 " + id + " 不是可显示的装扮或武器，已忽略"); continue; }
            if (!look.HasJob) notes.Add("试穿 " + id + " 没有 " + profile.JobTag + " 的外观块");
            chosen[look.Slot] = (id, "try-on");
        }
        // 武器：幻化 > 武器装扮 > 武器 > 默认。
        (int Id, string Source)? weapon = null;
        if (tryOn.Any(id => LookOf(id)?.Slot is 10 or 12)) weapon = chosen.TryGetValue(10, out var tw) && tw.Source == "try-on" ? tw : chosen[12];
        else if (input.WeaponSkin > 0 && LookOf(input.WeaponSkin) is { Slot: 12 }) weapon = (input.WeaponSkin, "weapon-skin");
        else if (chosen.TryGetValue(10, out var avatarWeapon)) weapon = avatarWeapon;
        else if (chosen.TryGetValue(12, out var realWeapon)) weapon = realWeapon;
        chosen.Remove(10);
        chosen.Remove(12);
        foreach (int id in profile.Defaults)
        {
            var look = LookOf(id);
            if (look is null) { notes.Add("默认外观 " + id + " 在 PVF 中不存在"); continue; }
            if (look.Slot == 12 || look.Slot == 10) { weapon ??= (id, "default"); continue; }
            if (look.Slot >= 0 && !chosen.ContainsKey(look.Slot)) chosen[look.Slot] = (id, "default");
        }
        if (weapon is { } w) chosen[12] = w;

        // 皮肤：皮肤装扮的 [variation] 决定 body 编号；没有皮肤时按 0 号（资源中的默认 body0000）。
        int bodyNumber = 0;
        ItemLook? skinLook = chosen.TryGetValue(8, out var skin) ? LookOf(skin.Id) : null;
        if (skinLook is { HasJob: true }) bodyNumber = skinLook.Number;
        else { skinLook = null; notes.Add("没有可用皮肤装扮，按 body0000 绘制"); }
        var draws = new List<(int Z, int Order, ImgFrame Frame)>();
        void Add(int slot, int id, string source, string layer, int z, IReadOnlyList<string> candidates, (string Img, int Palette)? found)
        {
            if (candidates.Count == 0)
            {
                layers.Add(new(slot, id, source, layer, z, "", profile.Frame, false, "层名无法按命名规则解析"));
                log?.Invoke($"纸娃娃：物品 {id} 层 {layer} 无法解析");
                return;
            }
            if (found is { } hit && images.TryReadFrame(hit.Img, profile.Frame, hit.Palette, out var frame) && frame is not null)
            {
                draws.Add((z, draws.Count, frame));
                layers.Add(new(slot, id, source, layer, z, hit.Img, profile.Frame, true, hit.Palette == 0 ? "" : "按调色板 " + hit.Palette + " 取色", hit.Palette));
                return;
            }
            string img = found?.Img ?? candidates[0];
            string note = found is null ? "ImagePacks2 中没有该 IMG" : "IMG 没有第 " + profile.Frame + " 帧或无法解码";
            layers.Add(new(slot, id, source, layer, z, img, profile.Frame, false, note, found?.Palette ?? 0));
            log?.Invoke($"纸娃娃：{note}：{img}（物品 {id} 层 {layer}）");
        }
        {
            string body = profile.BodyPath.Replace("%04d", bodyNumber.ToString("D4", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            (string, int)? hit = images.PaletteCount(body) >= 0 ? (body, 0) : null;
            if (hit is null && skinLook is { Minor: > 0 })
            {
                string major = profile.BodyPath.Replace("%04d", (skinLook.Major * 100).ToString("D4", CultureInfo.InvariantCulture), StringComparison.Ordinal);
                if (images.PaletteCount(major) > skinLook.Minor) hit = (major, skinLook.Minor);
            }
            Add(8, chosen.TryGetValue(8, out var s8) ? s8.Id : 0, chosen.ContainsKey(8) ? s8.Source : "body", "body", BodyZ, [body], hit);
        }
        foreach (var (slot, (id, source)) in chosen.OrderBy(pair => pair.Key))
        {
            if (slot == 8) continue;
            var look = LookOf(id);
            if (look is null) { notes.Add($"部位 {slot} 物品 {id} 在 PVF 中不存在"); continue; }
            if (!look.HasJob || look.Layers.Count == 0) { notes.Add($"部位 {slot} 物品 {id} 没有 {profile.JobTag} 的图层"); continue; }
            foreach (var (z, layer) in look.Layers)
                Add(slot, id, source, layer, z, ImgCandidates(profile, slot == 12, layer, look.Number), Resolve(profile, slot == 12, layer, look));
        }
        if (draws.Count == 0) return new(null, 0, 0, profile.Frame, profile.JobTag, layers, notes);

        // 各层帧共用同一张角色画布（maxW×maxH）；按 z 升序、同 z 先皮肤后装扮叠放，再裁到非透明范围。
        int left = draws.Min(d => d.Frame.KeyX), top = draws.Min(d => d.Frame.KeyY);
        int right = draws.Max(d => d.Frame.KeyX + d.Frame.Width), bottom = draws.Max(d => d.Frame.KeyY + d.Frame.Height);
        int width = right - left + Padding * 2, height = bottom - top + Padding * 2;
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048) { notes.Add("合成画布尺寸异常"); return new(null, 0, 0, profile.Frame, profile.JobTag, layers, notes); }
        var canvas = new byte[width * height * 4];
        foreach (var draw in draws.OrderBy(d => d.Z).ThenBy(d => d.Order))
            NpkImageDecoder.Blit(canvas, width, height, draw.Frame.Rgba, draw.Frame.Width, draw.Frame.Height,
                draw.Frame.KeyX - left + Padding, draw.Frame.KeyY - top + Padding);
        return new(PngEncoder.EncodeRgba(width, height, canvas), width, height, profile.Frame, profile.JobTag, layers, notes);
    }
}
