using ICSharpCode.SharpZipLib.BZip2;

namespace Meteo.Core.Dwd;

internal static class Bz2
{
    public static byte[] Decompress(Stream input)
    {
        using var bz = new BZip2InputStream(input);
        using var ms = new MemoryStream();
        bz.CopyTo(ms);
        return ms.ToArray();
    }
}
