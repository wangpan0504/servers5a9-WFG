using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class DocumentReadDiagnosticsTests
{
    internal static void Run()
    {
        var calls = new List<(bool Import, PvfDocumentReadMetrics Metrics)>();
        var reads = new List<string>();
        string Read(string path)
        {
            reads.Add(path);
            return path == "equipment/child.equ" ? "[creation rate]\n0\n[import script]\n`parent.equ`" : "[creation rate]\n7\n[name]\n`parent`";
        }
        var original = EquipmentFile.ReadScript(Read, "equipment/child.equ");
        string[] expectedReads = reads.ToArray(); reads.Clear();
        var profiled = EquipmentFile.ReadScriptProfiled(Read, "equipment/child.equ", (imported, value) => calls.Add((imported, value)));
        That(reads.SequenceEqual(expectedReads) && calls.Count == 2 && !calls[0].Import && calls[1].Import,
            "诊断保持根与导入读取顺序和次数");
        That(profiled.Section("creation rate") == original.Section("creation rate") && profiled.Section("name") == original.Section("name"),
            "诊断不改变显式零覆盖和父字段继承");
        That(calls.All(row => row.Metrics.ReadCompleted && row.Metrics.IndexCompleted && row.Metrics.ReadTicks >= 0 && row.Metrics.IndexTicks >= 0),
            "成功读取和字段索引分别计时");
        That(EquipmentFile.ReadScriptProfiled(Read, "equipment/child.equ", (_, _) => throw new Exception("observer")).Section("name") == original.Section("name"),
            "失败回调不改变成功解析结果");
        var failure = new InvalidDataException("original");
        PvfDocumentReadMetrics observed = default;
        Exception? caught = null;
        try { PvfDocumentReader.ReadProfiled(_ => throw failure, "root", false, (_, value) => { observed = value; throw new Exception("observer"); }); }
        catch (Exception error) { caught = error; }
        That(ReferenceEquals(caught, failure) && !observed.ReadCompleted && !observed.IndexCompleted && observed.IndexTicks == 0 && observed.IndexAllocatedBytes == 0,
            "读取失败保留原异常对象，不执行字段索引，回调失败不覆盖异常");
        caught = null;
        try { PvfDocumentReader.ReadProfiled(_ => null!, "root", false, (_, value) => observed = value); }
        catch (Exception error) { caught = error; }
        That(caught is ArgumentNullException && observed.ReadCompleted && !observed.IndexCompleted, "空文本读取成功但索引失败分开记录");
        foreach (string invalid in new[] { "[import script]\n`child.equ`", "[import script]\n`parent.equ`\n[import script]\n`other.equ`", "[import script]\ninvalid" })
        {
            Exception? plainError = null, profiledError = null;
            try { EquipmentFile.ReadScript(_ => invalid, "equipment/child.equ"); }
            catch (Exception error) { plainError = error; }
            try { EquipmentFile.ReadScriptProfiled(_ => invalid, "equipment/child.equ", (_, _) => throw new Exception("observer")); }
            catch (Exception error) { profiledError = error; }
            That(plainError is InvalidDataException && profiledError is not null && profiledError.GetType() == plainError.GetType() && profiledError.Message == plainError.Message,
                "循环、重复及畸形导入保持原拒绝与错误内容，失败回调不掩盖校验");
        }
        var missing = new FileNotFoundException("original missing");
        caught = null;
        try { EquipmentFile.ReadScriptProfiled(_ => throw missing, "equipment/missing.equ", (_, _) => throw new Exception("observer")); }
        catch (Exception error) { caught = error; }
        That(caught is InvalidDataException && ReferenceEquals(caught.InnerException, missing), "诊断保留原缺失文件包装及内部异常对象");
    }
}
