// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Message text → FT8/FT4 GFSK audio, ported from the former
// native/ft8/zeus_ft8.c (zeus_ft8_synth, gfsk_pulse, synth_gfsk — the WSJT-X algorithm
// as in ft8_lib's demo/gen_ft8.c). Continuous-phase GFSK: per-sample phase
// increments from the tone sequence convolved with a Gaussian pulse three
// symbols long, dummy symbols flattening the edge tails, and a raised-cosine
// ramp over the first and last eighth of a symbol. Float arithmetic, as the C.

namespace Zeus.Server.Hosting.Digital.Ft8;

public static class FtxSynth
{
    /// <summary>π·sqrt(2/ln 2), the Gaussian pulse constant.</summary>
    private const float GfskConstK = 5.336446f;
    private const float Ft8SymbolBt = 2.0f;
    private const float Ft4SymbolBt = 1.0f;

    /// <summary>Samples in one transmission: FT8 79 × 160 ms, FT4 105 × 48 ms.</summary>
    public static int WaveSamples(bool isFt4, int sampleRate)
    {
        int nSym = isFt4 ? FtxConstants.Ft4Nn : FtxConstants.Ft8Nn;
        float period = isFt4 ? FtxConstants.Ft4SymbolPeriod : FtxConstants.Ft8SymbolPeriod;
        return nSym * SamplesPerSymbol(sampleRate, period);
    }

    private static int SamplesPerSymbol(int rate, float period) => (int)(0.5f + rate * period);

    /// <summary>
    /// Encode <paramref name="message"/> and synthesize its waveform with tone 0
    /// at <paramref name="audioHz"/>. Full-scale ±1.0 — the caller scales.
    /// Null when the message does not encode (<paramref name="rc"/> says why).
    /// Callsigns in the message are saved into <paramref name="hash"/>, as the
    /// native synth saved them into its shared table.
    /// </summary>
    public static float[]? Synth(string message, bool isFt4, float audioHz, int sampleRate,
                                 IFtxCallsignHash? hash, out FtxMessageRc rc)
    {
        var payload = new byte[FtxMessage.PayloadBytes];
        rc = FtxMessage.Encode(message, hash, payload);
        if (rc != FtxMessageRc.Ok || sampleRate <= 0) return null;

        byte[] tones;
        float period, bt;
        if (isFt4)
        {
            tones = new byte[FtxConstants.Ft4Nn];
            FtxEncoder.Ft4Tones(payload, tones);
            period = FtxConstants.Ft4SymbolPeriod;
            bt = Ft4SymbolBt;
        }
        else
        {
            tones = new byte[FtxConstants.Ft8Nn];
            FtxEncoder.Ft8Tones(payload, tones);
            period = FtxConstants.Ft8SymbolPeriod;
            bt = Ft8SymbolBt;
        }

        return SynthGfsk(tones, audioHz, bt, period, sampleRate);
    }

    /// <summary>gfsk_pulse(): Gaussian-smoothed frequency pulse, 3 symbols long.</summary>
    internal static void GfskPulse(int nSpsym, float symbolBt, Span<float> pulse)
    {
        for (int i = 0; i < 3 * nSpsym; i++)
        {
            float t = i / (float)nSpsym - 1.5f;
            float arg1 = GfskConstK * symbolBt * (t + 0.5f);
            float arg2 = GfskConstK * symbolBt * (t - 0.5f);
            pulse[i] = (Erf(arg1) - Erf(arg2)) / 2.0f;
        }
    }

    /// <summary>synth_gfsk().</summary>
    internal static float[] SynthGfsk(ReadOnlySpan<byte> symbols, float f0, float symbolBt,
                                      float symbolPeriod, int rate)
    {
        int nSym = symbols.Length;
        int nSpsym = SamplesPerSymbol(rate, symbolPeriod);
        int nWave = nSym * nSpsym;

        var dphi = new float[nWave + 2 * nSpsym];
        var pulse = new float[3 * nSpsym];
        GfskPulse(nSpsym, symbolBt, pulse);

        const float hmod = 1.0f;
        float dphiPeak = 2.0f * MathF.PI * hmod / nSpsym;
        float dphiBase = 2.0f * MathF.PI * f0 / rate;
        for (int i = 0; i < dphi.Length; i++) dphi[i] = dphiBase;

        for (int i = 0; i < nSym; i++)
        {
            int ib = i * nSpsym;
            for (int j = 0; j < 3 * nSpsym; j++)
                dphi[ib + j] += dphiPeak * symbols[i] * pulse[j];
        }
        // Dummy symbols before and after (same tone as the first/last real one)
        // keep the edges on frequency.
        for (int j = 0; j < 2 * nSpsym; j++)
        {
            dphi[j] += dphiPeak * pulse[j + nSpsym] * symbols[0];
            dphi[nWave + j] += dphiPeak * pulse[j] * symbols[nSym - 1];
        }

        var signal = new float[nWave];
        float phi = 0.0f;
        for (int k = 0; k < nWave; k++)
        {
            signal[k] = MathF.Sin(phi);
            phi = CFmodf(phi + dphi[k + nSpsym], 2.0f * MathF.PI);
        }

        // Raised-cosine ramp against key clicks.
        int nRamp = nSpsym / 8;
        for (int i = 0; i < nRamp; i++)
        {
            float env = (1.0f - MathF.Cos(2.0f * MathF.PI * i / (2.0f * nRamp))) / 2.0f;
            signal[i] *= env;
            signal[nWave - 1 - i] *= env;
        }
        return signal;
    }

    /// <summary>
    /// The instantaneous phase (radians, in double) of the GFSK waveform
    /// <see cref="SynthGfsk"/> produces for <paramref name="symbols"/> — the
    /// same pulse shaping, without the amplitude ramp. The decoder uses it to
    /// rebuild a decoded signal as exp(jφ) and subtract it.
    /// </summary>
    internal static double[] GfskPhase(ReadOnlySpan<byte> symbols, double f0, float symbolBt,
                                       float symbolPeriod, int rate)
    {
        int nSym = symbols.Length;
        int nSpsym = SamplesPerSymbol(rate, symbolPeriod);
        int nWave = nSym * nSpsym;

        var pulse = new float[3 * nSpsym];
        GfskPulse(nSpsym, symbolBt, pulse);

        var dphi = new double[nWave + 2 * nSpsym];
        double dphiPeak = 2.0 * Math.PI / nSpsym;
        double dphiBase = 2.0 * Math.PI * f0 / rate;
        for (int i = 0; i < dphi.Length; i++) dphi[i] = dphiBase;
        for (int i = 0; i < nSym; i++)
            for (int j = 0; j < 3 * nSpsym; j++)
                dphi[i * nSpsym + j] += dphiPeak * symbols[i] * pulse[j];
        for (int j = 0; j < 2 * nSpsym; j++)
        {
            dphi[j] += dphiPeak * pulse[j + nSpsym] * symbols[0];
            dphi[nWave + j] += dphiPeak * pulse[j] * symbols[nSym - 1];
        }

        var phi = new double[nWave];
        double p = 0;
        for (int k = 0; k < nWave; k++)
        {
            phi[k] = p;
            p += dphi[k + nSpsym];
        }
        return phi;
    }

    /// <summary>Symbol BT product of the GFSK pulse: FT8 2.0, FT4 1.0.</summary>
    internal static float SymbolBt(bool isFt4) => isFt4 ? Ft4SymbolBt : Ft8SymbolBt;

    /// <summary>C fmodf: exact remainder with the sign of the dividend
    /// (C#'s % on floats is the same IEEE fmod).</summary>
    private static float CFmodf(float x, float y) => x % y;

    /// <summary>erff(). The error function in double, rounded to float — the
    /// Maclaurin series below |x| = 3, a continued fraction for erfc above.
    /// Both are good to ~1e-15, far inside float precision.</summary>
    internal static float Erf(float xf)
    {
        double x = xf;
        double ax = Math.Abs(x);
        double r;
        if (ax < 3.0)
        {
            // erf(x) = 2/√π Σ (-1)^n x^(2n+1) / (n! (2n+1))
            double x2 = x * x, term = x, sum = x;
            for (int n = 1; n < 200; n++)
            {
                term *= -x2 / n;
                double add = term / (2 * n + 1);
                sum += add;
                if (Math.Abs(add) < 1e-17 * Math.Abs(sum)) break;
            }
            r = 2.0 / Math.Sqrt(Math.PI) * sum;
        }
        else
        {
            // erfc(x) = e^(-x²)/√π · 1/(x + 1/2/(x + 1/(x + 3/2/(x + ...)))), Lentz.
            const double tiny = 1e-300;
            double f = ax, c = ax, d = 0;
            for (int n = 1; n < 500; n++)
            {
                double an = n / 2.0;
                d = ax + an * d;
                if (Math.Abs(d) < tiny) d = tiny;
                c = ax + an / c;
                if (Math.Abs(c) < tiny) c = tiny;
                d = 1 / d;
                double delta = c * d;
                f *= delta;
                if (Math.Abs(delta - 1) < 1e-16) break;
            }
            double erfc = Math.Exp(-ax * ax) / Math.Sqrt(Math.PI) / f;
            r = x > 0 ? 1 - erfc : erfc - 1;
        }
        return (float)r;
    }
}
