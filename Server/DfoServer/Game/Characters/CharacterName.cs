using System.Text;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Characters;

internal static class CharacterName
{
    // Client 02680C88 -> 03BE26C0 -> 03C78420；06477520 的代码页为 936。
    public static readonly Encoding Encoding = CreateEncoding();
    static Encoding CreateEncoding()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static bool IsValid(string name)
    {
        if (!Regex.IsMatch(name, "\\A[A-Za-z0-9_\u4e00-\u9fff]{1,12}\\z"))
            return false;
        try
        {
            return Encoding.GetByteCount(name) <= 12;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
