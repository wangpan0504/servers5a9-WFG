using System.Text;
using PvfLib;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Length != 3 || args[0] is not ("generate" or "verify" or "generate-targets" or "verify-targets" or "generate-all" or "verify-all" or "generate-revival" or "verify-revival" or "generate-complete" or "verify-complete"))
    throw new ArgumentException("Usage: FiendwarResourcePatch <generate|verify|generate-targets|verify-targets|generate-all|verify-all|generate-revival|verify-revival|generate-complete|verify-complete> base.pvf raw-directory-or-candidate.pvf");
using var archive = PvfArchive.Open(args[1]);
bool complete = args[0].EndsWith("-complete", StringComparison.Ordinal);
bool all = args[0].EndsWith("-all", StringComparison.Ordinal);
bool targets = args[0].EndsWith("-targets", StringComparison.Ordinal);
bool revival = args[0].EndsWith("-revival", StringComparison.Ordinal);
bool verify = args[0].StartsWith("verify", StringComparison.Ordinal);
var patches = complete ? PrisonPatch.Prepare(archive, allowPatched: true)
        .Concat(TargetMarkPatch.Prepare(archive, allowPatched: true))
        .Concat(RevivalPatch.Prepare(archive, allowPatched: true)).ToArray() :
    revival ? RevivalPatch.Prepare(archive, allowPatched: true).ToArray() :
    all ? PrisonPatch.Prepare(archive, allowPatched: true).Concat(TargetMarkPatch.Prepare(archive, allowPatched: true)).ToArray() :
    targets ? TargetMarkPatch.Prepare(archive).ToArray() : PrisonPatch.Prepare(archive).ToArray();
// 完整配套先规划三组资源，再一次输出清单；原十项及独立复活命令的合同保持不变。
ResourceDelta.Run(archive, patches, args[2], verify, combined: complete || all || revival);
