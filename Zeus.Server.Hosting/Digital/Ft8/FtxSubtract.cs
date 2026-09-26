// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Subtraction of a decoded FT8/FT4 signal from the slot audio, so the next
// decoding pass can find what it was hiding — the technique of WSJT-X's
// subtractft8, adapted to Zeus's real 12 kHz audio.
//
//   1. Rebuild the exact tone sequence from the decoded 77-bit payload.
//   2. Refine start time and frequency: the candidate search only resolves
//      half a symbol and a third of a tone. Search at a baseband decimated to
//      32 samples per symbol for the offset and frequency that maximise the
//      per-symbol energy of the known tones.
//   3. Reference c(t) = exp(jφ(t)), φ the GFSK phase of those tones. For a real
//      signal s = Re(a·c), s·conj(c) = a/2 + (a*/2)·conj(c)², so low-passing
//      it gives the slowly varying complex amplitude a/2 (the second term sits
//      at twice the audio frequency). Subtract 2·Re(lowpass · c).
//
// The low-pass is a triangle (two moving averages) normalised by the same
// filter run over the signal's extent, so the amplitude is not underestimated
// where the window hangs over the start or end of the transmission.

namespace Zeus.Server.Hosting.Digital.Ft8;

internal static class FtxSubtract
{
    private const int Rate = FtxDecoder.DecodeRate;
    private const int SamplesPerSymbolBb = 32;

    /// <summary>Remove the transmission <paramref name="payload"/> decoded at
    /// <paramref name="freqHz"/> (tone 0) and <paramref name="timeSec"/> from
    /// <paramref name="sig"/> (12 kHz, modified in place).</summary>
    public static void Subtract(float[] sig, bool isFt4, ReadOnlySpan<byte> payload, float freqHz, float timeSec)
    {
        int nTones = isFt4 ? 4 : 8;
        int nSym = isFt4 ? FtxConstants.Ft4Nn : FtxConstants.Ft8Nn;
        float period = isFt4 ? FtxConstants.Ft4SymbolPeriod : FtxConstants.Ft8SymbolPeriod;
        int nsps = (int)(0.5f + Rate * period);                // 1920 FT8, 576 FT4
        double spacing = 1.0 / period;                         // 6.25 / 20.833 Hz

        var tones = new byte[nSym];
        if (isFt4) FtxEncoder.Ft4Tones(payload, tones);
        else FtxEncoder.Ft8Tones(payload, tones);

        var (start, f0) = Refine(sig, tones, nTones, nsps, spacing, freqHz, timeSec);
        double[] phi = FtxSynth.GfskPhase(tones, f0, FtxSynth.SymbolBt(isFt4), period, Rate);
        int filt = isFt4 ? 1400 : 4000;                        // low-pass length, samples
        SubtractAt(sig, phi, start, filt);
    }

    /// <summary>Best start sample and tone-0 frequency near the candidate's.</summary>
    internal static (int Start, double F0) Refine(float[] sig, ReadOnlySpan<byte> tones, int nTones, int nsps,
                                                  double spacing, float freqHz, float timeSec)
    {
        int nSym = tones.Length;
        int dec = nsps / SamplesPerSymbolBb;                   // 60 FT8, 18 FT4
        double fsBb = (double)Rate / dec;
        double centre = freqHz + (nTones - 1) / 2.0 * spacing;

        // Baseband around the signal's centre, boxcar-decimated. The mixer
        // phasor advances by recurrence, renormalised every block.
        int nBb = sig.Length / dec;
        var bbRe = new double[nBb];
        var bbIm = new double[nBb];
        double w = -2.0 * Math.PI * centre / Rate;
        double stepRe = Math.Cos(w), stepIm = Math.Sin(w);
        for (int m = 0; m < nBb; m++)
        {
            double a = w * (m * dec);
            double pr = Math.Cos(a), pi = Math.Sin(a);
            double re = 0, im = 0;
            for (int j = 0; j < dec; j++)
            {
                double x = sig[m * dec + j];
                re += x * pr;
                im += x * pi;
                double t = pr * stepRe - pi * stepIm;
                pi = pr * stepIm + pi * stepRe;
                pr = t;
            }
            bbRe[m] = re;
            bbIm[m] = im;
        }

        // Rotation tables: for each frequency step and tone, e^{-j2πf·i/fsBb}.
        const int dfSteps = 13;                                 // ±½ tone in 1/12-tone steps
        var rotRe = new double[dfSteps * nTones * SamplesPerSymbolBb];
        var rotIm = new double[dfSteps * nTones * SamplesPerSymbolBb];
        for (int d = 0; d < dfSteps; d++)
            for (int t = 0; t < nTones; t++)
            {
                double f = (t - (nTones - 1) / 2.0) * spacing + (d - dfSteps / 2) * spacing / 12.0;
                for (int i = 0; i < SamplesPerSymbolBb; i++)
                {
                    double a = -2.0 * Math.PI * f * i / fsBb;
                    int idx = (d * nTones + t) * SamplesPerSymbolBb + i;
                    rotRe[idx] = Math.Cos(a);
                    rotIm[idx] = Math.Sin(a);
                }
            }

        // The offset (± a symbol and a quarter) and frequency that put the most
        // energy in the known tones, summed per symbol (phase-insensitive).
        int m0 = (int)Math.Round(timeSec * fsBb);
        int bestM = m0, bestD = dfSteps / 2;
        double best = -1;
        for (int d = 0; d < dfSteps; d++)
        {
            for (int dm = -SamplesPerSymbolBb - 8; dm <= SamplesPerSymbolBb + 8; dm++)
            {
                int ms = m0 + dm;
                double metric = 0;
                for (int k = 0; k < nSym; k++)
                {
                    int b0 = ms + k * SamplesPerSymbolBb;
                    if (b0 < 0 || b0 + SamplesPerSymbolBb > nBb) continue;
                    int r = (d * nTones + tones[k]) * SamplesPerSymbolBb;
                    double sr = 0, si = 0;
                    for (int i = 0; i < SamplesPerSymbolBb; i++)
                    {
                        double xr = bbRe[b0 + i], xi = bbIm[b0 + i];
                        double c = rotRe[r + i], sn = rotIm[r + i];
                        sr += xr * c - xi * sn;
                        si += xr * sn + xi * c;
                    }
                    metric += sr * sr + si * si;
                }
                if (metric > best)
                {
                    best = metric;
                    bestM = ms;
                    bestD = d;
                }
            }
        }
        double bestDf = (bestD - dfSteps / 2) * spacing / 12.0;
        double f0 = freqHz + bestDf;

        // Fine timing at the full rate, within ± half a baseband sample, with
        // the same per-symbol metric on the known tones.
        var toneRe = new double[nTones * nsps];
        var toneIm = new double[nTones * nsps];
        for (int t = 0; t < nTones; t++)
            for (int i = 0; i < nsps; i++)
            {
                double a = -2.0 * Math.PI * (f0 + t * spacing) * i / Rate;
                toneRe[t * nsps + i] = Math.Cos(a);
                toneIm[t * nsps + i] = Math.Sin(a);
            }
        int coarse = bestM * dec, bestStart = coarse;
        best = -1;
        int fineStep = Math.Max(1, dec / 12);
        for (int ds = -dec / 2; ds <= dec / 2; ds += fineStep)
        {
            int st = coarse + ds;
            double metric = 0;
            for (int k = 0; k < nSym; k++)
            {
                int b0 = st + k * nsps;
                if (b0 < 0 || b0 + nsps > sig.Length) continue;
                int r = tones[k] * nsps;
                double sr = 0, si = 0;
                for (int i = 0; i < nsps; i++)
                {
                    double x = sig[b0 + i];
                    sr += x * toneRe[r + i];
                    si += x * toneIm[r + i];
                }
                metric += sr * sr + si * si;
            }
            if (metric > best)
            {
                best = metric;
                bestStart = st;
            }
        }
        return (bestStart, f0);
    }

    /// <summary>Subtract the signal whose phase is <paramref name="phi"/>,
    /// starting at sample <paramref name="start"/>, from <paramref name="sig"/>.</summary>
    internal static void SubtractAt(float[] sig, double[] phi, int start, int filt)
    {
        int nWave = phi.Length;
        int half = filt / 2;
        int pad = filt;
        int len = nWave + 2 * pad;

        // z = s·conj(c) and the indicator of where the signal is, padded.
        var zr = new double[len];
        var zi = new double[len];
        var ind = new double[len];
        var cr = new double[nWave];
        var ci = new double[nWave];
        for (int i = 0; i < nWave; i++)
        {
            cr[i] = Math.Cos(phi[i]);
            ci[i] = Math.Sin(phi[i]);
            int n = start + i;
            if (n < 0 || n >= sig.Length) continue;
            zr[pad + i] = sig[n] * cr[i];
            zi[pad + i] = -sig[n] * ci[i];
            ind[pad + i] = 1;
        }

        // Triangle low-pass: two centred moving averages of half the length.
        MovingAverage(zr, half); MovingAverage(zr, half);
        MovingAverage(zi, half); MovingAverage(zi, half);
        MovingAverage(ind, half); MovingAverage(ind, half);

        for (int i = 0; i < nWave; i++)
        {
            int n = start + i;
            if (n < 0 || n >= sig.Length) continue;
            double norm = ind[pad + i];
            if (norm <= 1e-9) continue;
            double ar = zr[pad + i] / norm, ai = zi[pad + i] / norm;
            // 2·Re((ar + j·ai)·(cr + j·ci))
            sig[n] -= (float)(2.0 * (ar * cr[i] - ai * ci[i]));
        }
    }

    /// <summary>In place: y[i] = mean of x[i - w/2 .. i + w/2 - 1] (zeros outside).</summary>
    private static void MovingAverage(double[] x, int w)
    {
        var prefix = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++) prefix[i + 1] = prefix[i] + x[i];
        int lo = w / 2, hi = w - lo;
        for (int i = 0; i < x.Length; i++)
        {
            int a = Math.Max(0, i - lo), b = Math.Min(x.Length, i + hi);
            x[i] = (prefix[b] - prefix[a]) / w;
        }
    }
}
