using System.Text;
using PvfLib;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Length != 3 || args[0] is not ("generate" or "verify" or "generate-targets" or "verify-targets" or "generate-all" or "verify-all"))
    throw new ArgumentException("Usage: FiendwarResourcePatch <generate|verify|generate-targets|verify-targets|generate-all|verify-all> base.pvf raw-directory-or-candidate.pvf");
using var archive = PvfArchive.Open(args[1]);
bool all = args[0].EndsWith("-all", StringComparison.Ordinal);
bool targets = args[0].EndsWith("-targets", StringComparison.Ordinal);
bool verify = args[0].StartsWith("verify", StringComparison.Ordinal);
var patches = all ? PrisonPatch.Prepare(archive, allowPatched: true).Concat(TargetMarkPatch.Prepare(archive, allowPatched: true)).ToArray() :
    targets ? TargetMarkPatch.Prepare(archive).ToArray() : PrisonPatch.Prepare(archive).ToArray();
ResourceDelta.Run(archive, patches, args[2], verify, combined: all);
