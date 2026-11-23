namespace Meteo.Core.Grib;

/// <summary>
/// Decoder for CCSDS 121.0-B adaptive entropy coding (Rice / AEC) as used by GRIB2 data representation
/// template 5.42 (same bit-stream format as libaec). Supports unsigned samples up to 32 bits, with or without
/// the unit-delay preprocessor.
/// </summary>
public static class CcsdsDecoder
{
    public const int FlagSigned = 1;
    public const int Flag3Byte = 2;
    public const int FlagMsb = 4;
    public const int FlagPreprocess = 8;
    public const int FlagRestricted = 16;
    public const int FlagPadRsi = 32;

    private const int Ros = 5; // "remainder of segment" marker for zero blocks

    /// <summary>Decodes <paramref name="count"/> unsigned samples.</summary>
    public static uint[] Decode(ReadOnlySpan<byte> data, int count, int bitsPerSample, int flags, int blockSize, int rsi)
    {
        if ((flags & FlagSigned) != 0) throw new NotSupportedException("Signed CCSDS samples are not supported.");
        if (bitsPerSample < 1 || bitsPerSample > 32) throw new ArgumentOutOfRangeException(nameof(bitsPerSample));

        int n = bitsPerSample;
        int idLen = n > 16 ? 5 : n > 8 ? 4 : ((flags & FlagRestricted) != 0 && n <= 4 ? (n <= 2 ? 1 : 2) : 3);
        int uncompId = (1 << idLen) - 1;
        bool pp = (flags & FlagPreprocess) != 0;
        bool padRsi = (flags & FlagPadRsi) != 0;
        uint xmax = n == 32 ? uint.MaxValue : (1u << n) - 1;
        uint med = xmax / 2 + 1;

        var outp = new uint[count];
        var br = new BitReader(data);
        var block = new uint[blockSize];
        int outPos = 0;

        while (outPos < count)
        {
            // ---- one reference sample interval (RSI) ----
            int rsiSamples = 0;          // samples (incl. reference) produced in this RSI
            int rsiStart = outPos;
            uint last = 0;               // predictor state
            for (int b = 0; b < rsi && outPos < count; b++)
            {
                bool refBlock = pp && b == 0;
                int id = (int)br.Read(idLen);
                int produced; // number of entries written into 'block' (residuals or raw)
                bool raw = false;
                uint refSample = 0;

                if (id == 0)
                {
                    int selector = (int)br.Read(1);
                    if (refBlock) refSample = br.Read(n);
                    if (selector == 1)
                    {
                        // second extension: pairs coded with a single FS
                        int i = refBlock ? 1 : 0;
                        produced = 0;
                        while (i < blockSize)
                        {
                            int m = br.ReadFs();
                            int gi = (int)((Math.Sqrt(8.0 * m + 1) - 1) / 2);
                            while (gi * (gi + 1) / 2 > m) gi--;
                            while ((gi + 1) * (gi + 2) / 2 <= m) gi++;
                            int d1 = m - gi * (gi + 1) / 2;
                            if ((i & 1) == 0) { block[produced++] = (uint)(gi - d1); i++; }
                            block[produced++] = (uint)d1; i++;
                        }
                    }
                    else
                    {
                        // run of zero blocks
                        int zeroBlocks = br.ReadFs() + 1;
                        if (zeroBlocks == Ros)
                        {
                            int done = rsiSamples / blockSize;
                            zeroBlocks = Math.Min(rsi - done, 64 - done % 64);
                        }
                        else if (zeroBlocks > Ros) zeroBlocks--;

                        int zeros = zeroBlocks * blockSize - (refBlock ? 1 : 0);
                        if (refBlock) Emit(refSample, isRef: true);
                        for (int z = 0; z < zeros && outPos < count; z++) Emit(0, isRef: false);
                        rsiSamples += zeroBlocks * blockSize;
                        b += zeroBlocks - 1;
                        continue;
                    }
                }
                else if (id == uncompId)
                {
                    for (int s = 0; s < blockSize; s++) block[s] = br.Read(n);
                    produced = blockSize;
                    raw = true;
                }
                else
                {
                    int k = id - 1;
                    if (refBlock) refSample = br.Read(n);
                    int cnt = blockSize - (refBlock ? 1 : 0);
                    for (int s = 0; s < cnt; s++) block[s] = (uint)br.ReadFs() << k;
                    if (k > 0) for (int s = 0; s < cnt; s++) block[s] |= br.Read(k);
                    produced = cnt;
                }

                if (raw)
                {
                    // Uncompressed block: with preprocessing the first sample of the RSI is the reference.
                    for (int s = 0; s < produced && outPos < count; s++) Emit(block[s], isRef: refBlock && s == 0);
                }
                else
                {
                    if (refBlock) Emit(refSample, isRef: true);
                    for (int s = 0; s < produced && outPos < count; s++) Emit(block[s], isRef: false);
                }
                rsiSamples += blockSize;
            }
            if (padRsi) br.AlignToByte();
            if (outPos == rsiStart) break; // no progress (corrupt stream)

            void Emit(uint v, bool isRef)
            {
                if (!pp) { outp[outPos++] = v; return; }
                if (isRef) { last = v; outp[outPos++] = v; return; }
                // inverse of the CCSDS unit-delay mapping
                uint d = v;
                uint halfD = (d >> 1) + (d & 1);
                uint x = last;
                if (x < med)
                    x = halfD <= x ? ((d & 1) == 0 ? x + (d >> 1) : x - (d >> 1) - 1) : d;
                else
                    x = halfD <= xmax - x ? ((d & 1) == 0 ? x + (d >> 1) : x - (d >> 1) - 1) : xmax - d;
                last = x;
                outp[outPos++] = x;
            }
        }
        return outp;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;      // next byte to load
        private ulong _acc;    // bit accumulator, MSB-aligned at bit (_bits-1)
        private int _bits;     // number of valid bits in _acc

        public BitReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; _acc = 0; _bits = 0; }

        private void Fill()
        {
            while (_bits <= 56)
            {
                ulong b = _pos < _data.Length ? _data[_pos] : 0UL;
                _pos++;
                _acc = (_acc << 8) | b;
                _bits += 8;
            }
        }

        public uint Read(int count)
        {
            if (count == 0) return 0;
            if (_bits < count) Fill();
            _bits -= count;
            return (uint)((_acc >> _bits) & ((1UL << count) - 1));
        }

        /// <summary>Fundamental sequence: number of 0 bits before the next 1 bit.</summary>
        public int ReadFs()
        {
            int fs = 0;
            while (true)
            {
                if (_bits == 0) Fill();
                ulong window = _bits >= 64 ? _acc : _acc & ((1UL << _bits) - 1); // note: 1UL << 64 == 1 in C#
                if (window == 0) { fs += _bits; _bits = 0; if (_pos > _data.Length + 16) throw new InvalidDataException("CCSDS stream overrun."); continue; }
                int lead = System.Numerics.BitOperations.LeadingZeroCount(window) - (64 - _bits);
                fs += lead;
                _bits -= lead + 1;
                return fs;
            }
        }

        public void AlignToByte() => _bits -= _bits % 8;
    }
}
