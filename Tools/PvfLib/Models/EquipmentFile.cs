using System.Text.RegularExpressions;
using PvfLib.Scripting;

namespace PvfLib;

// 附加整数保留资源原值，不映射成客户端装备槽或装备类型枚举。
public sealed record EquipmentTypeDeclaration(string Label, int Parameter);

/// <summary>单份装备脚本的原始声明，不补业务缺省值，也不合并导入字段。</summary>
public sealed record EquipmentFile(int TemplateId, string ScriptPath,
    int? DeclaredMinimumLevel, int? DeclaredMaximumDurability, int? DeclaredWeight,
    int? DeclaredPrice, int? DeclaredSellPrice, int? DeclaredRequiredSkill,
    IReadOnlyList<string>? DeclaredUsableJobs, EquipmentTypeDeclaration? DeclaredEquipmentType,
    IReadOnlyList<string> UnsupportedFields)
{
    public static EquipmentFile Parse(int templateId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var fields = new PvfTokenFields(tokens, resolve,
            ["minimum level", "durability", "weight", "price", "sell price", "required skill", "usable job", "equipment type"]);
        int? minimum = fields.Scalar("minimum level"), durability = fields.Scalar("durability"), weight = fields.Scalar("weight"),
            price = fields.Scalar("price"), sellPrice = fields.Scalar("sell price"), requiredSkill = fields.Scalar("required skill");
        IReadOnlyList<string>? jobs = null;
        if (fields.TryGet("usable job", out var values))
        {
            bool stringsOnly = values.Length > 0;
            foreach (var token in values)
            {
                if (token.Type != 6)
                    stringsOnly = false;
            }
            if (stringsOnly)
            {
                var labels = new string[values.Length];
                for (int i = 0; i < labels.Length; i++)
                    labels[i] = resolve(values[i].Value);
                if (labels.All(label => !string.IsNullOrWhiteSpace(label)) && labels.Distinct(StringComparer.Ordinal).Count() == labels.Length)
                    jobs = Array.AsReadOnly(labels);
            }
            if (jobs is null)
                fields.Unsupported("usable job");
        }
        EquipmentTypeDeclaration? type = null;
        if (fields.TryGet("equipment type", out values))
        {
            if (values.Length == 2 && values[0].Type == 6 && values[1].Type == 0 && values[1].Value >= 0 &&
                !string.IsNullOrWhiteSpace(resolve(values[0].Value)))
                type = new(resolve(values[0].Value), values[1].Value);
            else
                fields.Unsupported("equipment type");
        }
        return new(templateId, path, minimum, durability, weight, price, sellPrice, requiredSkill, jobs, type, fields.UnsupportedFields);
    }

    /// <summary>读取装备的有效脚本视图；导入相对 equipment 根目录，本地声明覆盖父模板。</summary>
    public static PvfDocument ReadScript(Func<string, string> readText, string path)
    {
        path = PvfArchive.NormalizePath(path);
        var document = Read(path);
        if (document.DeclarationCount("import script") == 0)
            return document;
        var chain = new HashSet<string>(StringComparer.Ordinal) { path };
        var documents = new List<PvfDocument> { document };
        int imports;
        while ((imports = document.DeclarationCount("import script")) != 0)
        {
            var reference = Regex.Match(document.Section("import script"), @"^\s*`([^`]+)`\s*$");
            if (imports != 1 || !reference.Success || string.IsNullOrWhiteSpace(reference.Groups[1].Value))
                throw new InvalidDataException("装备模板导入路径无效: " + path);
            path = PvfArchive.NormalizePath("equipment/" + reference.Groups[1].Value);
            if (!chain.Add(path))
                throw new InvalidDataException("装备模板循环导入: " + path);
            document = Read(path);
            documents.Add(document);
        }
        // 导入位置不影响覆盖顺序；共享只读字段索引，不再重新扫描子脚本。
        var result = documents[^1];
        for (int i = documents.Count - 2; i >= 0; i--)
            result = documents[i].WithParent(result);
        return result;

        PvfDocument Read(string source)
        {
            try { return new PvfDocument(readText(source)); }
            catch (FileNotFoundException exception)
            {
                throw new InvalidDataException("装备模板文件缺失: " + source, exception);
            }
        }
    }
}
