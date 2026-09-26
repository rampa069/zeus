// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// FT8/FT4 slot decoder, ported from ft8_lib's ft8/decode.c (candidate search
// on the Costas sync, soft demodulation, LDPC + CRC; Karlis Goba, MIT, see
// LICENSE.ft8_lib) and the former native/ft8/zeus_ft8.c (zeus_ft8_decode:
// resampling, the pipeline and its settings, de-duplication, and the SNR
// estimate).
//
// One pass is exactly what the native decoder did, which the golden tests
// (TestData/ft8) hold it to. More passes subtract what was decoded and search
// again (FtxSubtract), as WSJT-X does.

namespace Zeus.Server.Hosting.Digital.Ft8;

/// <summary>One decoded message: SNR in the 2500 Hz reference bandwidth, time
/// offset into the slot, audio frequency of tone 0, sync score and text.</summary>
public readonly record struct FtxDecode(int SnrDb, float DtSec, float FreqHz, int Score, string Text);

public static class FtxDecoder
{
    public const int DecodeRate = 12_000;

    private const int MaxCandidates = 140;
    private const int LdpcIters = 20;
    private const int MinScore = 10;
    private const int MaxDecodes = 64;

    private struct Candidate
    {
        public short Score;
        public short TimeOffset;
        public short FreqOffset;
        public byte TimeSub;
        public byte FreqSub;
    }

    /// <summary>
    /// zeus_ft8_decode(): decode one slot of mono audio at any sample rate
    /// (resampled to 12 kHz). Hashed callsigns resolve through, and every call
    /// seen is saved into, <paramref name="hash"/>.
    ///
    /// <paramref name="passes"/> &gt; 1 decodes again after subtracting what the
    /// previous pass decoded (FtxSubtract), so signals hidden under stronger
    /// ones come through; it stops early when a pass finds nothing new. One
    /// pass is exactly the native decoder's behaviour. <paramref name="onPass"/>
    /// receives each pass's new decodes as soon as it has them (pass 1 always,
    /// later passes only when they found something), so a caller can publish
    /// the first pass without waiting for the rest.
    /// </summary>
    public static List<FtxDecode> Decode(ReadOnlySpan<float> audio, int sampleRate, bool isFt4,
                                         IFtxCallsignHash? hash, int passes = 1,
                                         Action<int, IReadOnlyList<FtxDecode>>? onPass = null)
    {
        var output = new List<FtxDecode>();
        if (audio.Length == 0 || sampleRate <= 0) return output;

        float[] sig = ResampleTo12k(audio, sampleRate);
        if (sig.Length == 0) return output;

        passes = Math.Clamp(passes, 1, MaxPasses);
        for (int pass = 0; pass < passes && output.Count < MaxDecodes; pass++)
        {
            var fresh = DecodePass(sig, isFt4, hash, output);
            if (pass == 0 || fresh.Count > 0) onPass?.Invoke(pass + 1, fresh.Select(f => f.Decode).ToList());
            if (fresh.Count == 0 || pass == passes - 1) break;
            foreach (var (d, payload) in fresh)
                FtxSubtract.Subtract(sig, isFt4, payload, d.FreqHz, d.DtSec);
        }
        return output;
    }

    /// <summary>Most passes a caller may ask for (the UI offers 1-4).</summary>
    public const int MaxPasses = 4;

    /// <summary>One candidate search + decode over <paramref name="sig"/>.
    /// New messages are appended to <paramref name="output"/> and returned with
    /// their payloads, for subtraction.</summary>
    private static List<(FtxDecode Decode, byte[] Payload)> DecodePass(
        float[] sig, bool isFt4, IFtxCallsignHash? hash, List<FtxDecode> output)
    {
        var fresh = new List<(FtxDecode, byte[])>();
        var mon = new FtxMonitor(isFt4, DecodeRate);
        int framePos = 0;
        while (framePos + mon.BlockSize <= sig.Length)
        {
            mon.Process(sig.AsSpan(framePos, mon.BlockSize));
            framePos += mon.BlockSize;
            if (mon.Wf.NumBlocks >= mon.Wf.MaxBlocks) break;
        }

        var candidates = new Candidate[MaxCandidates];
        int nCand = FindCandidates(mon.Wf, candidates, MinScore);

        var payload = new byte[FtxMessage.PayloadBytes];
        for (int i = 0; i < nCand && output.Count < MaxDecodes; i++)
        {
            ref readonly Candidate cand = ref candidates[i];
            if (!DecodeCandidate(mon.Wf, cand, LdpcIters, payload)) continue;
            if (FtxMessage.Decode(payload, hash, out string text) != FtxMessageRc.Ok) continue;

            float freqHz = (mon.MinBin + cand.FreqOffset + (float)cand.FreqSub / mon.Wf.FreqOsr) / mon.SymbolPeriod;
            float timeSec = (cand.TimeOffset + (float)cand.TimeSub / mon.Wf.TimeOsr) * mon.SymbolPeriod;

            // The same message often wins several candidates (and, after a
            // subtraction, can leave a trace a later pass decodes again).
            bool dup = false;
            foreach (var d in output)
            {
                if (d.Text == text && MathF.Abs(d.FreqHz - freqHz) < 5.0f)
                {
                    dup = true;
                    break;
                }
            }
            if (dup) continue;

            int snr = EstimateSnrDb(sig, freqHz, timeSec, isFt4);
            int snrDb = snr == SnrUnknown ? (int)(cand.Score * 0.5f) - 26 : snr;
            var decode = new FtxDecode(snrDb, timeSec, freqHz, cand.Score, text);
            output.Add(decode);
            fresh.Add((decode, payload.ToArray()));
        }
        return fresh;
    }

    // ---- resampling ---------------------------------------------------------

    /// <summary>Linear interpolation to 12 kHz, in double, as the C. At 48 kHz
    /// the ratio is exactly 4, so this is plain decimation (no anti-alias
    /// filter; the DIGU RX filter is relied on).</summary>
    internal static float[] ResampleTo12k(ReadOnlySpan<float> input, int rateIn)
    {
        if (rateIn == DecodeRate) return input.ToArray();

        double ratio = (double)DecodeRate / rateIn;
        int n = (int)(input.Length * ratio);
        if (n <= 0) return [];

        var output = new float[n];
        for (int i = 0; i < n; i++)
        {
            double src = i / ratio;
            int i0 = (int)src;
            int i1 = i0 + 1;
            if (i1 >= input.Length) i1 = input.Length - 1;
            double frac = src - i0;
            output[i] = (float)((1.0 - frac) * input[i0] + frac * input[i1]);
        }
        return output;
    }

    // ---- candidate search ---------------------------------------------------

    /// <summary>Waterfall index of candidate symbol 0 — may be negative for
    /// candidates that start before the slot; callers only read in-range blocks.</summary>
    private static int CandBase(FtxWaterfall wf, in Candidate c)
    {
        int offset = c.TimeOffset;
        offset = offset * wf.TimeOsr + c.TimeSub;
        offset = offset * wf.FreqOsr + c.FreqSub;
        offset = offset * wf.NumBins + c.FreqOffset;
        return offset;
    }

    private static int Ft8SyncScore(FtxWaterfall wf, in Candidate c)
    {
        int score = 0, numAverage = 0;
        int baseIdx = CandBase(wf, c);
        byte[] mag = wf.Mag;
        int stride = wf.BlockStride;

        for (int m = 0; m < FtxConstants.Ft8NumSync; ++m)
        {
            for (int k = 0; k < FtxConstants.Ft8LengthSync; ++k)
            {
                int block = FtxConstants.Ft8SyncOffset * m + k;
                int blockAbs = c.TimeOffset + block;
                if (blockAbs < 0) continue;
                if (blockAbs >= wf.NumBlocks) break;

                int p8 = baseIdx + block * stride;
                int sm = FtxConstants.Ft8Costas[k];
                if (sm > 0)
                {
                    score += mag[p8 + sm] - mag[p8 + sm - 1];
                    ++numAverage;
                }
                if (sm < 7)
                {
                    score += mag[p8 + sm] - mag[p8 + sm + 1];
                    ++numAverage;
                }
                if (k > 0 && blockAbs > 0)
                {
                    score += mag[p8 + sm] - mag[p8 + sm - stride];
                    ++numAverage;
                }
                if (k + 1 < FtxConstants.Ft8LengthSync && blockAbs + 1 < wf.NumBlocks)
                {
                    score += mag[p8 + sm] - mag[p8 + sm + stride];
                    ++numAverage;
                }
            }
        }
        if (numAverage > 0) score /= numAverage;
        return score;
    }

    private static int Ft4SyncScore(FtxWaterfall wf, in Candidate c)
    {
        int score = 0, numAverage = 0;
        int baseIdx = CandBase(wf, c);
        byte[] mag = wf.Mag;
        int stride = wf.BlockStride;

        for (int m = 0; m < FtxConstants.Ft4NumSync; ++m)
        {
            for (int k = 0; k < FtxConstants.Ft4LengthSync; ++k)
            {
                int block = 1 + FtxConstants.Ft4SyncOffset * m + k;
                int blockAbs = c.TimeOffset + block;
                if (blockAbs < 0) continue;
                if (blockAbs >= wf.NumBlocks) break;

                int p4 = baseIdx + block * stride;
                int sm = FtxConstants.Ft4Costas[m * 4 + k];
                if (sm > 0)
                {
                    score += mag[p4 + sm] - mag[p4 + sm - 1];
                    ++numAverage;
                }
                if (sm < 3)
                {
                    score += mag[p4 + sm] - mag[p4 + sm + 1];
                    ++numAverage;
                }
                if (k > 0 && blockAbs > 0)
                {
                    score += mag[p4 + sm] - mag[p4 + sm - stride];
                    ++numAverage;
                }
                if (k + 1 < FtxConstants.Ft4LengthSync && blockAbs + 1 < wf.NumBlocks)
                {
                    score += mag[p4 + sm] - mag[p4 + sm + stride];
                    ++numAverage;
                }
            }
        }
        if (numAverage > 0) score /= numAverage;
        return score;
    }

    /// <summary>ftx_find_candidates(): the best candidates by sync score, kept
    /// in a min-heap and returned sorted best first.</summary>
    private static int FindCandidates(FtxWaterfall wf, Candidate[] heap, int minScore)
    {
        int numTones = wf.IsFt4 ? 4 : 8;
        int numCandidates = heap.Length;
        int heapSize = 0;
        var c = new Candidate();

        for (c.TimeSub = 0; c.TimeSub < wf.TimeOsr; ++c.TimeSub)
        {
            for (c.FreqSub = 0; c.FreqSub < wf.FreqOsr; ++c.FreqSub)
            {
                for (c.TimeOffset = -10; c.TimeOffset < 20; ++c.TimeOffset)
                {
                    for (c.FreqOffset = 0; c.FreqOffset + numTones - 1 < wf.NumBins; ++c.FreqOffset)
                    {
                        c.Score = (short)(wf.IsFt4 ? Ft4SyncScore(wf, c) : Ft8SyncScore(wf, c));
                        if (c.Score < minScore) continue;

                        // Full and better than the worst: drop the worst.
                        if (heapSize == numCandidates && c.Score > heap[0].Score)
                        {
                            --heapSize;
                            heap[0] = heap[heapSize];
                            HeapifyDown(heap, heapSize);
                        }
                        if (heapSize < numCandidates)
                        {
                            heap[heapSize] = c;
                            ++heapSize;
                            HeapifyUp(heap, heapSize);
                        }
                    }
                }
            }
        }

        // Heap sort: descending by score.
        int lenUnsorted = heapSize;
        while (lenUnsorted > 1)
        {
            (heap[lenUnsorted - 1], heap[0]) = (heap[0], heap[lenUnsorted - 1]);
            lenUnsorted--;
            HeapifyDown(heap, lenUnsorted);
        }
        return heapSize;
    }

    private static void HeapifyDown(Candidate[] heap, int heapSize)
    {
        int current = 0;
        while (true)
        {
            int left = 2 * current + 1;
            int right = left + 1;
            int smallest = current;
            if (left < heapSize && heap[left].Score < heap[smallest].Score) smallest = left;
            if (right < heapSize && heap[right].Score < heap[smallest].Score) smallest = right;
            if (smallest == current) break;
            (heap[smallest], heap[current]) = (heap[current], heap[smallest]);
            current = smallest;
        }
    }

    private static void HeapifyUp(Candidate[] heap, int heapSize)
    {
        int current = heapSize - 1;
        while (current > 0)
        {
            int parent = (current - 1) / 2;
            if (!(heap[current].Score < heap[parent].Score)) break;
            (heap[parent], heap[current]) = (heap[current], heap[parent]);
            current = parent;
        }
    }

    // ---- demodulation + LDPC ------------------------------------------------

    private static float Mag(byte x) => x * 0.5f - 120.0f;
    private static float Max2(float a, float b) => a >= b ? a : b;
    private static float Max4(float a, float b, float c, float d) => Max2(Max2(a, b), Max2(c, d));

    private static void Ft8ExtractSymbol(byte[] wf, int p, Span<float> logl)
    {
        Span<float> s2 = stackalloc float[8];
        for (int j = 0; j < 8; ++j) s2[j] = Mag(wf[p + FtxConstants.Ft8Gray[j]]);
        logl[0] = Max4(s2[4], s2[5], s2[6], s2[7]) - Max4(s2[0], s2[1], s2[2], s2[3]);
        logl[1] = Max4(s2[2], s2[3], s2[6], s2[7]) - Max4(s2[0], s2[1], s2[4], s2[5]);
        logl[2] = Max4(s2[1], s2[3], s2[5], s2[7]) - Max4(s2[0], s2[2], s2[4], s2[6]);
    }

    private static void Ft4ExtractSymbol(byte[] wf, int p, Span<float> logl)
    {
        Span<float> s2 = stackalloc float[4];
        for (int j = 0; j < 4; ++j) s2[j] = Mag(wf[p + FtxConstants.Ft4Gray[j]]);
        logl[0] = Max2(s2[2], s2[3]) - Max2(s2[0], s2[1]);
        logl[1] = Max2(s2[1], s2[3]) - Max2(s2[0], s2[2]);
    }

    private static void Ft8ExtractLikelihood(FtxWaterfall wf, in Candidate c, Span<float> log174)
    {
        int baseIdx = CandBase(wf, c);
        for (int k = 0; k < FtxConstants.Ft8Nd; ++k)
        {
            int symIdx = k + (k < 29 ? 7 : 14);           // skip the sync groups
            int bitIdx = 3 * k;
            int block = c.TimeOffset + symIdx;
            if (block < 0 || block >= wf.NumBlocks)
                log174.Slice(bitIdx, 3).Clear();
            else
                Ft8ExtractSymbol(wf.Mag, baseIdx + symIdx * wf.BlockStride, log174.Slice(bitIdx, 3));
        }
    }

    private static void Ft4ExtractLikelihood(FtxWaterfall wf, in Candidate c, Span<float> log174)
    {
        int baseIdx = CandBase(wf, c);
        for (int k = 0; k < FtxConstants.Ft4Nd; ++k)
        {
            int symIdx = k + (k < 29 ? 5 : k < 58 ? 9 : 13);
            int bitIdx = 2 * k;
            int block = c.TimeOffset + symIdx;
            if (block < 0 || block >= wf.NumBlocks)
                log174.Slice(bitIdx, 2).Clear();
            else
                Ft4ExtractSymbol(wf.Mag, baseIdx + symIdx * wf.BlockStride, log174.Slice(bitIdx, 2));
        }
    }

    private static void NormalizeLogl(Span<float> log174)
    {
        float sum = 0, sum2 = 0;
        for (int i = 0; i < FtxConstants.LdpcN; ++i)
        {
            sum += log174[i];
            sum2 += log174[i] * log174[i];
        }
        float invN = 1.0f / FtxConstants.LdpcN;
        float variance = (sum2 - sum * sum * invN) * invN;

        // Scale with ft8_lib's experimentally found coefficient.
        float normFactor = MathF.Sqrt(24.0f / variance);
        for (int i = 0; i < FtxConstants.LdpcN; ++i) log174[i] *= normFactor;
    }

    /// <summary>ftx_decode_candidate(): soft bits → LDPC → CRC. On success
    /// <paramref name="payload"/> holds the 77-bit message (FT4 un-whitened).</summary>
    private static bool DecodeCandidate(FtxWaterfall wf, in Candidate c, int maxIterations, Span<byte> payload)
    {
        Span<float> log174 = stackalloc float[FtxConstants.LdpcN];
        if (wf.IsFt4) Ft4ExtractLikelihood(wf, c, log174);
        else Ft8ExtractLikelihood(wf, c, log174);
        NormalizeLogl(log174);

        Span<byte> plain174 = stackalloc byte[FtxConstants.LdpcN];
        if (FtxLdpc.BpDecode(log174, maxIterations, plain174) > 0) return false;

        Span<byte> a91 = stackalloc byte[FtxConstants.LdpcKBytes];
        PackBits(plain174, FtxConstants.LdpcK, a91);

        ushort crcExtracted = FtxCrc.Extract(a91);
        a91[9] &= 0xF8;
        a91[10] = 0;
        ushort crcCalculated = FtxCrc.Compute(a91, 96 - 14);
        if (crcExtracted != crcCalculated) return false;

        for (int i = 0; i < 10; ++i)
            payload[i] = wf.IsFt4 ? (byte)(a91[i] ^ FtxConstants.Ft4Xor[i]) : a91[i];
        return true;
    }

    private static void PackBits(ReadOnlySpan<byte> bits, int numBits, Span<byte> packed)
    {
        int numBytes = (numBits + 7) / 8;
        packed[..numBytes].Clear();
        byte mask = 0x80;
        int byteIdx = 0;
        for (int i = 0; i < numBits; ++i)
        {
            if (bits[i] != 0) packed[byteIdx] |= mask;
            mask >>= 1;
            if (mask == 0)
            {
                mask = 0x80;
                ++byteIdx;
            }
        }
    }

    // ---- SNR ----------------------------------------------------------------
    //
    // ft8_lib's demo reports candidate score / 2 as "SNR", which is a
    // correlation score, always positive — ~28 dB high against the network.
    // WSJT-X reports SNR in a 2500 Hz noise bandwidth, so measure that: a Welch
    // power spectrum over the message, the signal as the power in the 50 Hz the
    // tones occupy, and the noise floor as the MEDIAN of the bins around it
    // (other FT8 signals in the passband do not drag a median), scaled to 2500 Hz.

    private const int SnrUnknown = -999;
    private const int SnrNfft = 4096;                 // 2.93 Hz bins at 12 kHz
    private const int SnrBins = SnrNfft / 2 + 1;
    private const float SnrRefBw = 2500.0f;
    private const float SigBw = 50.0f;                // 8 tones × 6.25 Hz

    [ThreadStatic] private static KissFftr? t_snrFft;

    private static int WelchPower(float[] sig, int from, int to, float[] power)
    {
        if (from < 0) from = 0;
        if (to > sig.Length) to = sig.Length;
        if (to - from < SnrNfft) return 0;

        var fft = t_snrFft ??= new KissFftr(SnrNfft);
        var win = new float[SnrNfft];
        var spec = new Cpx[SnrBins];
        Array.Clear(power);

        int blocks = 0;
        for (int start = from; start + SnrNfft <= to; start += SnrNfft / 2)
        {
            for (int i = 0; i < SnrNfft; i++)
            {
                float w = 0.5f - 0.5f * MathF.Cos(2.0f * MathF.PI * i / (SnrNfft - 1));
                win[i] = sig[start + i] * w;
            }
            fft.Transform(win, spec);
            for (int i = 0; i < SnrBins; i++) power[i] += spec[i].R * spec[i].R + spec[i].I * spec[i].I;
            blocks++;
        }
        if (blocks > 0)
            for (int i = 0; i < SnrBins; i++) power[i] /= blocks;
        return blocks;
    }

    /// <summary>estimate_snr_db(): SNR in dB (2500 Hz reference) of the
    /// transmission at <paramref name="freqHz"/> starting at <paramref name="timeSec"/>.</summary>
    private static int EstimateSnrDb(float[] sig, float freqHz, float timeSec, bool isFt4)
    {
        float slot = isFt4 ? 7.5f : 15.0f;
        float dur = isFt4 ? 4.48f : 12.64f;           // 105 × 0.048 / 79 × 0.16
        if (freqHz < 100.0f || freqHz > 2900.0f) return SnrUnknown;

        float t0 = timeSec;
        if (t0 < 0.0f) t0 = 0.0f;
        if (t0 > slot) return SnrUnknown;

        // Per call, not static as in the C (which made it non-reentrant).
        var power = new float[SnrBins];
        int blocks = WelchPower(sig, (int)(t0 * DecodeRate), (int)((t0 + dur) * DecodeRate), power);
        if (blocks == 0) return SnrUnknown;

        const float binHz = (float)DecodeRate / SnrNfft;
        int sigLo = (int)((freqHz - binHz) / binHz);
        int sigHi = (int)((freqHz + SigBw + binHz) / binHz);
        if (sigLo < 1) sigLo = 1;
        if (sigHi >= SnrBins) sigHi = SnrBins - 1;
        if (sigHi <= sigLo) return SnrUnknown;

        // Noise: median of the bins within ±400 Hz, minus the signal's span and
        // a guard band so a neighbour 100 Hz away is not taken as noise.
        var around = new float[SnrBins];
        int nAround = 0;
        int lo = sigLo - (int)(400.0f / binHz), hi = sigHi + (int)(400.0f / binHz);
        if (lo < 1) lo = 1;
        if (hi >= SnrBins) hi = SnrBins - 1;
        int guard = (int)(30.0f / binHz);
        for (int i = lo; i <= hi; i++)
        {
            if (i >= sigLo - guard && i <= sigHi + guard) continue;
            around[nAround++] = power[i];
        }
        if (nAround < 16) return SnrUnknown;
        Array.Sort(around, 0, nAround);
        float noisePerBin = around[nAround / 2];
        if (noisePerBin <= 0.0f) return SnrUnknown;

        float sigPower = 0.0f;
        for (int i = sigLo; i <= sigHi; i++) sigPower += power[i];
        sigPower -= noisePerBin * (sigHi - sigLo + 1);
        if (sigPower <= 0.0f) return -30;

        float noiseRef = noisePerBin * (SnrRefBw / binHz);
        float snr = 10.0f * MathF.Log10(sigPower / noiseRef);

        if (snr < -30.0f) snr = -30.0f;               // WSJT-X's own floor
        if (snr > 49.0f) snr = 49.0f;
        return (int)MathF.Round(snr);                 // lrintf: round half to even
    }
}
