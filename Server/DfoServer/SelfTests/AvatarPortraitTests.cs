using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Network.Management.Avatar;
using DfoServer.Network.Management.ImagePack;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 纸娃娃：IMG v4/v6 调色板解码（内存合成的小样本，不入库大文件）、PVF 图层解析与合成、appearance 只读命令。
internal static class AvatarPortraitTests
{
    public static void RunCore()
    {
        int before = Count;
        Decoder();
        Layers();
        Command();
        Console.WriteLine($"PASS {Count - before} 纸娃娃 IMG v4/v6 解码、图层解析、合成与外观命令断言");
    }

    // 构造 IMG：palettes 为 null 时写 v2；帧 (w,h,keyX,keyY,payload,compressed) 或 link 目标。
    internal static byte[] Img(int version, byte[][]? palettes, params object[] frames)
    {
        using var index = new MemoryStream();
        using var data = new MemoryStream();
        var w = new BinaryWriter(index);
        foreach (object frame in frames)
        {
            if (frame is int link) { w.Write(0x11); w.Write(link); continue; }
            var (width, height, keyX, keyY, payload, compressed) = ((int, int, int, int, byte[], bool))frame;
            byte[] stored = payload;
            if (compressed)
            {
                using var packed = new MemoryStream();
                using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) z.Write(payload);
                stored = packed.ToArray();
            }
            w.Write(0x0E); w.Write(compressed ? 6 : 5); w.Write(width); w.Write(height); w.Write(stored.Length);
            w.Write(keyX); w.Write(keyY); w.Write(500); w.Write(500);
            data.Write(stored);
        }
        w.Flush();
        using var output = new MemoryStream();
        var o = new BinaryWriter(output);
        o.Write("Neople Img File\0"u8.ToArray());
        o.Write((int)index.Length); o.Write(0); o.Write(version); o.Write(frames.Length);
        if (version == 4) WritePalette(o, palettes![0]);
        if (version == 6) { o.Write(palettes!.Length); foreach (var palette in palettes) WritePalette(o, palette); }
        o.Write(index.ToArray());
        o.Write(data.ToArray());
        return output.ToArray();
    }

    static void WritePalette(BinaryWriter o, byte[] rgba) { o.Write(rgba.Length / 4); o.Write(rgba); }

    static void Decoder()
    {
        byte[] palette = [0, 0, 0, 0, 255, 0, 0, 255, 0, 0, 255, 128];
        byte[] alt = [0, 0, 0, 0, 0, 255, 0, 255, 0, 0, 0, 255];
        var v4 = Img(4, [palette], (2, 2, 10, 20, new byte[] { 0, 1, 2, 1 }, true), 0);
        That(ImgFrameReader.TryRead(v4, 0, out var frame) && frame is { Width: 2, Height: 2, KeyX: 10, KeyY: 20, CanvasWidth: 500 } &&
            frame.Rgba.AsSpan(0, 4).SequenceEqual(new byte[4]) && frame.Rgba.AsSpan(4, 4).SequenceEqual(new byte[] { 255, 0, 0, 255 }) &&
            frame.Rgba.AsSpan(8, 4).SequenceEqual(new byte[] { 0, 0, 255, 128 }), "v4 单调色板：zlib 解压后 1 字节下标按 RGBA 取色并保留 key 坐标");
        That(ImgFrameReader.TryRead(v4, 1, out var linked) && linked!.Rgba.AsSpan(4, 4).SequenceEqual(new byte[] { 255, 0, 0, 255 }), "v4 链接帧指向原帧");
        var v6 = Img(6, [palette, alt], (1, 2, 0, 0, new byte[] { 1, 2 }, true), (1, 1, 3, 4, new byte[] { 0x00, 0xFC }, false));
        That(ImgFrameReader.TryRead(v6, 0, out var first) && first!.Rgba.AsSpan(0, 4).SequenceEqual(new byte[] { 255, 0, 0, 255 }), "v6 多调色板默认用第 0 个");
        That(ImgFrameReader.TryRead(v6, 0, out var second, 1) && second!.Rgba.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 255, 0, 255 }), "v6 可选其他调色板");
        That(ImgFrameReader.TryRead(v6, 1, out var direct) && direct is { Width: 1, KeyX: 3 } && direct.Rgba[0] == 255 && direct.Rgba[3] == 255,
            "v6 长度不等于 w×h 的未压缩 0x0E 帧按 ARGB1555 直接色解码");
        That(!ImgFrameReader.TryRead(v6, 2, out _) && !ImgFrameReader.TryRead(v6[..60], 0, out _) && !ImgFrameReader.TryRead(Img(5, [palette], 0), 0, out _),
            "越界帧、截断文件与未实现的 v5 返回失败而不抛出");
        // v2 未压缩帧按 w×h×2 计长度（头里声明的 size 不可信），后一帧的偏移不受影响。
        var v2 = Img(2, null, (1, 1, 0, 0, new byte[] { 0x00, 0xFC }, false), (1, 1, 0, 0, new byte[] { 0x1F, 0x80 }, false));
        BinaryPrimitives.WriteInt32LittleEndian(v2.AsSpan(32 + 16), 4);
        That(ImgFrameReader.TryRead(v2, 1, out var v2b) && v2b!.Rgba[2] == 255 && v2b.Rgba[0] == 0, "v2 未压缩帧忽略声明长度，第二帧取到正确像素");
        That(NpkImageDecoder.TryDecodeFrame(v2, 1, out int v2Width, out int v2Height, out var v2Icon) &&
            v2Width == 1 && v2Height == 1 && v2Icon.AsSpan().SequenceEqual(v2b!.Rgba), "v2 图标与纸娃娃读取第二个未压缩帧一致");
        That(!NpkImageDecoder.TryDecodeFrame(v2[..^1], 1, out _, out _, out _), "v2 截断像素不得当空白成功");
        var packedV2 = Img(2, null, (1, 1, 0, 0, new byte[] { 0x00, 0xFC }, true), 0);
        That(NpkImageDecoder.TryDecodeFrame(packedV2, 1, out _, out _, out var packedIcon) && packedIcon[0] == 255,
            "v2 压缩帧与链接帧保留红色像素");
        var cropped = Img(2, null, (2, 1, -1, 0, new byte[] { 0x00, 0xFC, 0x1F, 0x80 }, false));
        BinaryPrimitives.WriteInt32LittleEndian(cropped.AsSpan(32 + 28), 1);
        BinaryPrimitives.WriteInt32LittleEndian(cropped.AsSpan(32 + 32), 1);
        That(NpkImageDecoder.TryDecodeFrame(cropped, 0, out int croppedWidth, out int croppedHeight, out var croppedIcon) &&
            croppedWidth == 1 && croppedHeight == 1 && croppedIcon.AsSpan().SequenceEqual(new byte[] { 0, 0, 255, 255 }),
            "v2 图标保留小画布定位和负坐标裁剪");
        var loop = Img(4, [palette], 1, 0);
        That(!ImgFrameReader.TryRead(loop, 0, out _), "链接环被拒绝");
        // 旧解码入口（物品图标）对 v4/v6 也可用，并按 ≤256 画布放置。
        That(NpkImageDecoder.TryDecodeFrame(v4, 0, out int iconWidth, out _, out var icon) && iconWidth == 2 && icon.Length == 16, "图标解码入口转交 v4 调色板，超 256 的画布退回帧尺寸");
    }

    sealed class FakeImages(Dictionary<string, ImgFrame> frames, Dictionary<string, int> palettes) : IAvatarImages
    {
        public bool TryReadFrame(string imgPath, int frame, int palette, out ImgFrame? result)
        {
            bool ok = frames.TryGetValue(imgPath.ToLowerInvariant() + "#" + frame, out result);
            if (ok && palette > 0) result = result! with { Rgba = [(byte)palette, 0, 0, 255] };
            return ok;
        }
        public int PaletteCount(string imgPath) =>
            palettes.TryGetValue(imgPath.ToLowerInvariant(), out int count) ? count :
            frames.Keys.Any(key => key.StartsWith(imgPath.ToLowerInvariant() + "#", StringComparison.Ordinal)) ? 0 : -1;
    }

    static ImgFrame Solid(int x, int y, byte r) => new(1, 1, x, y, 500, 500, [r, 0, 0, 255]);

    static void Layers()
    {
        var pvf = new Dictionary<string, string>
        {
            ["character/character.lst"] = "#PVF_File\n0\t`Swordman/Swordman.chr`\n11\t`Swordman/ATSwordman.chr`\n13\t`DemonicLancer/DemonicLancer.chr`",
            ["character/swordman/swordman.chr"] = "[job]\n`[swordman]`\n[waiting motion]\n`Animation/Stay.ani`\n`Animation/Move.ani`\n[body image path]\n`Character/swordman/Equipment/Avatar/skin/sm_body%04d.img`",
            ["character/swordman/atswordman.chr"] = "[job]\n`[at swordman]`\n[waiting motion]\n`Animation/ATStay.ani`\n[body image path]\n`Character/swordman/ATEquipment/Avatar/skin/sg_body%04d.img`",
            ["character/demoniclancer/demoniclancer.chr"] = "[job]\n`[demonic lancer]`\n[body image path]\n`Character/demoniclancer/Equipment/Avatar/skin/dl_body%04d.img`",
            ["character/swordman/animation/stay.ani"] = "[FRAME MAX]\n4\n[FRAME000]\n[IMAGE]\n`Character/Swordman/Equipment/Avatar/skin/sm_body%04d.img`\n90\n[IMAGE POS]\n-232\t-333",
            ["character/characteretc/swordman.etc"] = "[equipment index]\n`defaultEquipment`\t-1\t11\t-1\t12\t-1\t-1\t-1\t-1\t13\t-1\t14\n[/equipment index]",
            ["character/characteretc/atswordman.etc"] = "[key value index]\n`defaultEquipment`\t-1\t0\t-1\t1\t31\t11\t14\n[/key value index]",
            ["equipment/hair.equ"] = "[equipment type]\n`[hair avatar]`\t0\n[animation job]\n`[swordman]`\n[variation]\n0\t0\n[layer variation]\n2000\t`hair_a`\n[animation job]\n`[at swordman]`\n[variation]\n0\t0\n[layer variation]\n2000\t`hair_a`",
            ["equipment/coat.equ"] = "[equipment type]\n`[coat avatar]`\t0\n[animation job]\n`[swordman]`\n[variation]\n0\t0\n[layer variation]\n1800\t`coat_a`",
            ["equipment/coat110.equ"] = "[name]\n`新衣`\n[animation job]\n`[swordman]`\n[variation]\n110\t0\n[layer variation]\n2300\t`coat_c`\n[equipment ani script]\n`equipment/character/swordman.lay`\n[layer variation]\n1800\t`coat_a`\n[equipment type]\n`[coat avatar]`\t0",
            ["equipment/skin.equ"] = "[equipment type]\n`[skin avatar]`\t0\n[animation job]\n`[swordman]`\n[variation]\n0\t0\n[animation job]\n`[demonic swordman]`\n[variation]\n0\t1000",
            ["equipment/skin2.equ"] = "[equipment type]\n`[skin avatar]`\t0\n[animation job]\n`[swordman]`\n[variation]\n0\t4",
            ["equipment/weapon.equ"] = "[equipment type]\n`[weapon]`\t17\n[animation job]\n`[swordman]`\n[variation]\n0\t0\n[layer variation]\n2790\t`sswdc`\n[layer variation]\n650\t`sswdb`\n[animation job]\n`[at swordman]`\n[variation]\n0\t0\n[layer variation]\n2150\t`at_sswda`",
            ["equipment/club.equ"] = "[equipment type]\n`[weapon avatar]`\t0\n[animation job]\n`[swordman]`\n[expand ani]\n[variation]\n30\t0\n[layer variation]\n2790\t`clubc`\n[/expand ani]",
            ["equipment/skinweapon.equ"] = "[equipment type]\n`[weapon]`\t17\n[animation job]\n`[swordman]`\n[variation]\n2\t5\n[layer variation]\n2790\t`katanac`",
            ["equipment/odd.equ"] = "[equipment type]\n`[hat avatar]`\t0\n[animation job]\n`[swordman]`\n[variation]\n1\t0\n[layer variation]\n2500\t`cap`\n[layer variation]\n2400\t`cap_b`",
            ["equipment/dyed.equ"] = "[equipment type]\n`[weapon]`\t17\n[animation job]\n`[swordman]`\n[variation]\n0\t3\n[layer variation]\n2790\t`sswdc`",
            ["equipment/rod.equ"] = "[equipment type]\n`[weapon]`\t17\n[animation job]\n`[swordman]`\n[variation]\n0\t0\n[layer variation]\n2790\t`rodc`",
            ["equipment/dyed9.equ"] = "[equipment type]\n`[weapon]`\t17\n[animation job]\n`[swordman]`\n[variation]\n0\t9\n[layer variation]\n2790\t`sswdc`",
            ["equipment/child.equ"] = "[import script]\n`coat.equ`\n[name]\n`子装扮`",
        };
        var ids = new Dictionary<int, string> { [11] = "hair", [12] = "coat", [13] = "skin", [14] = "weapon", [15] = "coat110", [16] = "skin2", [17] = "club", [18] = "skinweapon", [19] = "odd", [20] = "child", [21] = "dyed", [22] = "rod", [23] = "dyed9", [31] = "hair" };
        const string Avatar = "character/swordman/equipment/avatar/", Weapon = "character/swordman/equipment/weapon/";
        var frames = new Dictionary<string, ImgFrame>
        {
            [Avatar + "skin/sm_body0000.img#90"] = Solid(100, 100, 10),
            [Avatar + "skin/sm_body0004.img#90"] = Solid(100, 100, 11),
            [Avatar + "hair/sm_hair0000a.img#90"] = Solid(100, 100, 20),
            [Avatar + "coat/sm_coat0000a.img#90"] = Solid(100, 100, 30),
            [Avatar + "coat/sm_coat11000a.img#90"] = Solid(100, 100, 31),
            [Avatar + "coat/sm_coat11000c.img#90"] = Solid(100, 100, 32),
            [Weapon + "sswd/sswd0000c.img#90"] = Solid(100, 100, 40),
            [Weapon + "sswd/sswd0000b.img#90"] = Solid(98, 101, 41),
            [Weapon + "club/club3000c.img#90"] = Solid(100, 100, 42),
            [Weapon + "katana/katana0205c.img#90"] = Solid(100, 100, 43),
            [Weapon + "rod/sm_rod0000c.img#90"] = Solid(100, 100, 44),
            ["character/swordman/atequipment/avatar/skin/sg_body0000.img#0"] = Solid(0, 0, 50),
            ["character/swordman/atequipment/weapon/sswd/sg_sswd0000a.img#0"] = Solid(0, 0, 51),
        };
        var images = new FakeImages(frames, new() { [Weapon + "sswd/sswd0000c.img"] = 6 });
        var logs = new List<string>();
        var builder = new AvatarPortraitBuilder(path => pvf.GetValueOrDefault(path), id => ids.TryGetValue(id, out var name) ? "equipment/" + name + ".equ" : null, images, logs.Add);
        AvatarLookInput Look(byte job, int skin = 0, params AvatarWorn[] worn) => new("Hero", job, 0, worn, skin);
        static string Draw(AvatarPortrait p) => string.Join(",", p.Layers.Where(l => l.Drawn).OrderBy(l => l.Z).Select(l => l.Layer + "@" + l.Source));

        var naked = builder.Build(Look(0), []);
        That(naked.Frame == 90 && naked.JobTag == "[swordman]" && naked.Png is not null && naked.Width == 3 + 8 && naked.Height == 2 + 8,
            "站立帧取自 [waiting motion] 首个 ani 的 [IMAGE] 编号；画布裁到图层范围加 4 像素边距");
        That(Draw(naked) == "sswdb@default,body@default,coat_a@default,hair_a@default,sswdc@default",
            "空身按 characteretc defaultEquipment 补发型、上衣、皮肤与默认武器，并按 z 排序（皮肤暂定 z=1000）");

        var worn = builder.Build(Look(0, 0, new AvatarWorn(3, 15, 0), new AvatarWorn(8, 16, 0)), []);
        That(Draw(worn).Contains("coat_a@worn") && Draw(worn).Contains("coat_c@worn") && worn.Layers.Any(l => l.Img.EndsWith("sm_body0004.img") && l.Drawn) &&
            worn.Layers.Any(l => l.Img == "Character/swordman/Equipment/Avatar/coat/sm_coat11000c.img"),
            "variation 110 0 → 编号 11000（a×100+b），皮肤 variation 0 4 → body0004；实穿覆盖默认");

        var clone = builder.Build(Look(0, 0, new AvatarWorn(3, 999, 15)), []);
        That(Draw(clone).Contains("coat_c@clone") && clone.Layers.All(l => l.ItemId != 999), "克隆装扮按 ClearAvatarId 画外观物品");

        var weapons = builder.Build(Look(0, 18, new AvatarWorn(10, 17, 0), new AvatarWorn(12, 14, 0)), []);
        That(Draw(weapons).Contains("katanac@weapon-skin") && !Draw(weapons).Contains("clubc") && !Draw(weapons).Contains("sswd"), "武器幻化优先于武器装扮与武器");
        var avatarWeapon = builder.Build(Look(0, 0, new AvatarWorn(10, 17, 0), new AvatarWorn(12, 14, 0)), []);
        That(Draw(avatarWeapon).Contains("clubc@worn") && !Draw(avatarWeapon).Contains("sswd"), "无幻化时武器装扮优先于武器；[expand ani] 内的层同样解析");
        var badSkin = builder.Build(Look(0, 15, new AvatarWorn(12, 14, 0)), []);
        That(Draw(badSkin).Contains("sswdc@worn"), "幻化编号不是武器时忽略，画实际武器");

        var trial = builder.Build(Look(0, 0, new AvatarWorn(3, 12, 0)), [15, 17, 999]);
        That(Draw(trial).Contains("coat_c@try-on") && Draw(trial).Contains("clubc@try-on") && trial.Notes.Any(n => n.Contains("999")),
            "试穿按物品自身部位在内存中替换，未知编号忽略并说明");

        var dyed = builder.Build(Look(0, 0, new AvatarWorn(12, 21, 0)), []);
        That(dyed.Layers.Single(l => l.Layer == "sswdc") is { Drawn: true, Palette: 3 } layer && layer.Img.EndsWith("sswd0000c.img"),
            "variation 0 3 没有 0003 号文件时取 0000 号文件的第 3 个调色板");
        var dyed9 = builder.Build(Look(0, 0, new AvatarWorn(12, 23, 0)), []);
        That(dyed9.Layers.Single(l => l.Layer == "sswdc") is { Drawn: false } miss9 && miss9.Img.EndsWith("sswd0009c.img") && miss9.Note.Contains("没有该 IMG"),
            "调色板数不够时不借用，按缺失跳过");
        var rod = builder.Build(Look(0, 0, new AvatarWorn(12, 22, 0)), []);
        That(rod.Layers.Single(l => l.Layer == "rodc") is { Drawn: true } rodLayer && rodLayer.Img.EndsWith("Weapon/rod/sm_rod0000c.img"),
            "无职业前缀的武器层在不带前缀的文件不存在时改用皮肤前缀");

        var odd = builder.Build(Look(0, 0, new AvatarWorn(0, 19, 0), new AvatarWorn(3, 20, 0)), []);
        That(odd.Layers.Any(l => l.Layer == "cap" && !l.Drawn && l.Note.Contains("命名规则")) &&
            odd.Layers.Any(l => l.Layer == "cap_b" && !l.Drawn && l.Img.EndsWith("sm_cap0100b.img")) && logs.Count >= 2 &&
            Draw(odd).Contains("coat_a@worn"), "无法解析的层名与缺失的 IMG 跳过并记日志；import script 子脚本沿父脚本取图层");

        var at = builder.Build(Look(11), []);
        That(at.Frame == 0 && at.Notes.Any(n => n.Contains("站立动画")) && Draw(at) == "body@body,at_sswda@default" &&
            at.Layers.Any(l => l.Img.EndsWith("ATEquipment/Weapon/sswd/sg_sswd0000a.img")) &&
            at.Layers.Any(l => l.Layer == "hair_a" && l.ItemId == 31),
            "key value index 写法的默认外观按物品自身部位归位；武器职业前缀换成皮肤文件前缀");
        var lancer = builder.Build(Look(13), []);
        That(lancer.Png is null && lancer.Notes.Any(n => n.Contains("characteretc/demoniclancer.etc")), "缺少 characteretc 的职业如实说明没有默认外观");
        That(builder.Build(Look(5), []) is { Png: null } missing && missing.Notes.Any(n => n.Contains("职业 5")), "character.lst 没有的职业返回说明");
    }

    static void Command()
    {
        using var f = new MailboxPersistenceTests.Fixture();
        f.OtherOwner.Dispose();
        string Answer(string line) => DevelopmentGm.Answer(line, "other", f.Repository, f.Owners, _ => null);
        long revision;
        using (var lease = f.Owners.Acquire("other", () => { })) revision = f.Repository.LoadCharacter(lease, 0).Revision;
        string text = Answer("appearance Other");
        That(text.StartsWith("OK ", StringComparison.Ordinal), "appearance 只读命令成功: " + text);
        var root = JsonDocument.Parse(text[3..]).RootElement;
        That(root.GetProperty("name").GetString() == "Other" && root.GetProperty("rev").GetInt64() == revision &&
            root.GetProperty("equipment").ValueKind == JsonValueKind.Array && root.GetProperty("weaponSkin").GetInt32() == 0 &&
            root.TryGetProperty("job", out _) && root.TryGetProperty("growType", out _), "appearance 返回职业、装备位与幻化选择，不递增 revision");
        That(root.GetProperty("equipment").EnumerateArray().All(row => row.GetProperty("slot").GetInt32() is >= 0 and <= 12), "只列 0–12 号装备位");
        That(Answer("appearance Nobody").StartsWith("ERR ") && Answer("appearance Other 1").StartsWith("ERR ") && Answer("appearance").StartsWith("ERR "), "appearance 参数严格");
    }
}
