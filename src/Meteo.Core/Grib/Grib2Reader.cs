using System.Buffers.Binary;

namespace Meteo.Core.Grib;

/// <summary>One decoded GRIB2 field on a regular lat/lon grid (template 3.0).</summary>
public sealed class Grib2Field
{
    public required DateTime ReferenceTimeUtc { get; init; }
    public required int Discipline { get; init; }
    public required int ParameterCategory { get; init; }
    public required int ParameterNumber { get; init; }
    public required int ProductTemplate { get; init; }
    public required int ForecastTime { get; init; }
    public required int ForecastTimeUnit { get; init; }
    public required int FirstSurfaceType { get; init; }

    /// <summary>Forecast lead time converted to hours (time unit 0=min, 1=h, 2=day).</summary>
    public double ForecastHours => ForecastTimeUnit switch { 0 => ForecastTime / 60.0, 1 => ForecastTime, 2 => ForecastTime * 24.0, 13 => ForecastTime / 3600.0, _ => ForecastTime };
    public required double FirstSurfaceValue { get; init; }

    public required int Ni { get; init; }
    public required int Nj { get; init; }
    /// <summary>Latitude of first grid point (degrees).</summary>
    public required double Lat1 { get; init; }
    public required double Lon1 { get; init; }
    public required double Lat2 { get; init; }
    public required double Lon2 { get; init; }
    public required double Di { get; init; }
    public required double Dj { get; init; }
    public required int ScanMode { get; init; }

    /// <summary>Values in row-major order as stored in the file (NaN = missing).</summary>
    public required float[] Values { get; init; }
}

/// <summary>
/// Minimal GRIB2 decoder sufficient for DWD ICON open data: grid template 3.0 (regular lat/lon),
/// data representation templates 5.0 (simple packing, ICON-D2) and 5.42 (CCSDS/AEC, ICON-EU), optional bitmap.
/// </summary>
public static class Grib2Reader
{
    public static IReadOnlyList<Grib2Field> Read(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    public static IReadOnlyList<Grib2Field> Read(ReadOnlySpan<byte> data)
    {
        var result = new List<Grib2Field>();
        int pos = 0;
        while (pos + 16 <= data.Length)
        {
            if (!(data[pos] == 'G' && data[pos + 1] == 'R' && data[pos + 2] == 'I' && data[pos + 3] == 'B'))
            {
                pos++;
                continue;
            }
            int edition = data[pos + 7];
            if (edition != 2) throw new NotSupportedException($"GRIB edition {edition} not supported.");
            int discipline = data[pos + 6];
            long total = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos + 8, 8));
            var msg = data.Slice(pos, (int)total);
            result.AddRange(ReadMessage(msg, discipline));
            pos += (int)total;
        }
        return result;
    }

    private static IEnumerable<Grib2Field> ReadMessage(ReadOnlySpan<byte> msg, int discipline)
    {
        var fields = new List<Grib2Field>();
        int p = 16;
        DateTime refTime = default;
        GridInfo? grid = null;
        ProductInfo? prod = null;
        PackingInfo? pack = null;
        byte[]? bitmap = null;

        while (p + 4 <= msg.Length)
        {
            if (msg[p] == '7' && msg[p + 1] == '7' && msg[p + 2] == '7' && msg[p + 3] == '7') break;
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(msg.Slice(p, 4));
            int num = msg[p + 4];
            var s = msg.Slice(p, len);
            switch (num)
            {
                case 1:
                    refTime = new DateTime(BinaryPrimitives.ReadUInt16BigEndian(s.Slice(12, 2)), s[14], s[15], s[16], s[17], s[18], DateTimeKind.Utc);
                    break;
                case 3:
                    grid = ParseGrid(s);
                    break;
                case 4:
                    prod = ParseProduct(s);
                    break;
                case 5:
                    pack = ParsePacking(s);
                    break;
                case 6:
                    int ind = s[5];
                    if (ind == 0) bitmap = s.Slice(6).ToArray();
                    else if (ind == 255) bitmap = null;
                    // 254 = reuse previous bitmap
                    break;
                case 7:
                    if (grid is null || prod is null || pack is null)
                        throw new InvalidDataException("GRIB2 data section before grid/product/packing sections.");
                    var values = Unpack(s.Slice(5), pack.Value, grid.Value.Ni * grid.Value.Nj, bitmap);
                    var g = grid.Value; var pr = prod.Value;
                    fields.Add(new Grib2Field
                    {
                        ReferenceTimeUtc = refTime,
                        Discipline = discipline,
                        ParameterCategory = pr.Category,
                        ParameterNumber = pr.Number,
                        ProductTemplate = pr.Template,
                        ForecastTime = pr.ForecastTime,
                        ForecastTimeUnit = pr.TimeUnit,
                        FirstSurfaceType = pr.SurfaceType,
                        FirstSurfaceValue = pr.SurfaceValue,
                        Ni = g.Ni, Nj = g.Nj,
                        Lat1 = g.Lat1, Lon1 = g.Lon1, Lat2 = g.Lat2, Lon2 = g.Lon2,
                        Di = g.Di, Dj = g.Dj, ScanMode = g.ScanMode,
                        Values = values,
                    });
                    break;
            }
            p += len;
        }
        return fields;
    }

    private readonly record struct GridInfo(int Ni, int Nj, double Lat1, double Lon1, double Lat2, double Lon2, double Di, double Dj, int ScanMode);
    private readonly record struct ProductInfo(int Template, int Category, int Number, int TimeUnit, int ForecastTime, int SurfaceType, double SurfaceValue);
    private readonly record struct PackingInfo(int Template, int NumPoints, float R, int E, int D, int Bits,
        int CcsdsFlags = 0, int BlockSize = 0, int Rsi = 0);

    private static GridInfo ParseGrid(ReadOnlySpan<byte> s)
    {
        int tmpl = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(12, 2));
        if (tmpl != 0) throw new NotSupportedException($"GRIB2 grid template 3.{tmpl} not supported (need regular lat/lon).");
        int ni = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(30, 4));
        int nj = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(34, 4));
        double lat1 = SignMag32(s.Slice(46, 4)) * 1e-6;
        double lon1 = SignMag32(s.Slice(50, 4)) * 1e-6;
        double lat2 = SignMag32(s.Slice(55, 4)) * 1e-6;
        double lon2 = SignMag32(s.Slice(59, 4)) * 1e-6;
        double di = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(63, 4)) * 1e-6;
        double dj = BinaryPrimitives.ReadUInt32BigEndian(s.Slice(67, 4)) * 1e-6;
        int scan = s[71];
        if (lon1 > 180) lon1 -= 360;
        if (lon2 > 180) lon2 -= 360;
        return new GridInfo(ni, nj, lat1, lon1, lat2, lon2, di, dj, scan);
    }

    private static ProductInfo ParseProduct(ReadOnlySpan<byte> s)
    {
        int tmpl = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(7, 2));
        int cat = s[9], num = s[10];
        int unit = s[17];
        int ft = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(18, 4));
        int surf = s[22];
        int scaleF = (sbyte)s[23];
        long scaled = SignMag32(s.Slice(24, 4));
        double sv = scaled == unchecked((int)0xFFFFFFFF) ? double.NaN : scaled / Math.Pow(10, scaleF);
        return new ProductInfo(tmpl, cat, num, unit, ft, surf, sv);
    }

    private static PackingInfo ParsePacking(ReadOnlySpan<byte> s)
    {
        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(5, 4));
        int tmpl = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(9, 2));
        if (tmpl != 0 && tmpl != 42)
            throw new NotSupportedException($"GRIB2 data representation template 5.{tmpl} not supported (only 5.0 and 5.42).");
        float r = BinaryPrimitives.ReadSingleBigEndian(s.Slice(11, 4));
        int e = SignMag16(s.Slice(15, 2));
        int d = SignMag16(s.Slice(17, 2));
        int bits = s[19];
        if (tmpl == 42)
            return new PackingInfo(tmpl, n, r, e, d, bits, s[21], s[22], BinaryPrimitives.ReadUInt16BigEndian(s.Slice(23, 2)));
        return new PackingInfo(tmpl, n, r, e, d, bits);
    }

    private static float[] Unpack(ReadOnlySpan<byte> data, PackingInfo pk, int gridPoints, byte[]? bitmap)
    {
        var result = new float[gridPoints];
        double r = pk.R;
        double bscale = Math.Pow(2, pk.E);
        double dscale = Math.Pow(10, -pk.D);
        int bits = pk.Bits;

        uint[]? ccsds = null;
        if (pk.Template == 42 && bits > 0)
            ccsds = CcsdsDecoder.Decode(data, pk.NumPoints, bits, pk.CcsdsFlags, pk.BlockSize, pk.Rsi);

        long bitPos = 0;
        int packed = 0;
        for (int i = 0; i < gridPoints; i++)
        {
            if (bitmap != null && (bitmap[i >> 3] & (0x80 >> (i & 7))) == 0)
            {
                result[i] = float.NaN;
                continue;
            }
            ulong x;
            if (bits == 0) x = 0;
            else if (ccsds != null) x = ccsds[packed++];
            else { x = ReadBits(data, bitPos, bits); bitPos += bits; }
            result[i] = (float)((r + x * bscale) * dscale);
        }
        return result;
    }

    private static ulong ReadBits(ReadOnlySpan<byte> data, long bitPos, int count)
    {
        ulong v = 0;
        long byteIdx = bitPos >> 3;
        int bitOff = (int)(bitPos & 7);
        int remaining = count;
        while (remaining > 0)
        {
            int avail = 8 - bitOff;
            int take = Math.Min(avail, remaining);
            int b = data[(int)byteIdx];
            int shifted = (b >> (avail - take)) & ((1 << take) - 1);
            v = (v << take) | (uint)shifted;
            remaining -= take;
            bitOff = 0;
            byteIdx++;
        }
        return v;
    }

    private static int SignMag32(ReadOnlySpan<byte> b)
    {
        uint u = BinaryPrimitives.ReadUInt32BigEndian(b);
        if (u == 0xFFFFFFFF) return unchecked((int)u);
        int mag = (int)(u & 0x7FFFFFFF);
        return (u & 0x80000000) != 0 ? -mag : mag;
    }

    private static int SignMag16(ReadOnlySpan<byte> b)
    {
        ushort u = BinaryPrimitives.ReadUInt16BigEndian(b);
        int mag = u & 0x7FFF;
        return (u & 0x8000) != 0 ? -mag : mag;
    }
}
