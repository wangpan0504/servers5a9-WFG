using System.Text;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ArchiveHeaderFormatTests
{
    public static void Run()
    {
        byte[] guard = ArchiveFixture.Create(false, ("header.txt", 3, Encoding.Unicode.GetBytes("甲A")));
        byte[] plain = (byte[])guard.Clone();
        // JP导出不加guard；流加密按异或工作，仅撤销原编码器的四字节guard。
        for (int index = 24; index < 28; index++)
            plain[index] ^= 0x55;
        byte[] cn = ArchiveFixture.Create(true, ("header.txt", 3, Encoding.Unicode.GetBytes("甲A")));
        foreach (var (bytes, format) in new[] { (guard, "nkpi"), (cn, "protected_nkpi"), (plain, "nkpi") })
        {
            using (var archive = PvfArchive.OpenBytes(bytes))
                That(archive.Format == format && archive.FileCount == 1 && archive.GroupCount == 1 &&
                    archive.ReadText("header.txt") == "甲A", "JP guard、CN和JP无guard均按完整头边界选格式并保留正文");

            Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bytes.AsSpan(0, bytes.Length - 1)));
            byte[] bad = (byte[])bytes.Clone();
            bad[0] ^= 1;
            Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bad));
            foreach (int offset in new[] { 24, 32, 36, 40, 44 })
            {
                bad = (byte[])bytes.Clone();
                bad[offset + 3] ^= 0x80;
                Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bad));
                bad = (byte[])bytes.Clone();
                bad[offset + 3] ^= 0x40;
                Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bad));
                // 声明仍在512MiB内，但表或正文越过实际小归档，magic不得提前认定成功。
                bad = (byte[])bytes.Clone();
                bad[offset + 3] ^= 0x01;
                Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bad));
            }
            bad = (byte[])bytes.Clone();
            ArchiveFixture.Put(bad, 48 + 8, 1);
            Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bad));
        }
    }
}
