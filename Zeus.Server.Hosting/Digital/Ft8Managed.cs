// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital — FT8/FT4 decode and synth (Digital/Ft8, a C# port of ft8_lib
// and of the former native zeus_ft8.c shim), for the pipeline and the keyer.
// One callsign table is shared by decode and synth for the whole session, as
// the native library's static table was:
// a non-standard call spelled out in one slot resolves hashed references to
// it in later ones, including in what we send.

using Zeus.Server.Hosting.Digital.Ft8;

namespace Zeus.Server.Hosting.Digital;

public static class Ft8Managed
{
    private const int MaxDecodesPerSlot = 64;

    /// <summary>The session-long callsign hash table.</summary>
    internal static FtxCallsignTable Table { get; } = new();

    /// <summary>Always true: nothing to load.</summary>
    public static bool Available => true;

    /// <summary>Decode one slot of mono audio at any rate, mapped to the wire
    /// DTO as the native wrapper mapped it (dt to 0.01 s, frequency to 1 Hz).</summary>
    /// <paramref name="passes"/> &gt; 1 decodes again after subtracting what was
    /// found; <paramref name="onPass"/> gets each pass's new decodes as they come.
    public static IReadOnlyList<Ft8DecodeDto> Decode(float[] audio, int rate, bool isFt4, int passes = 1,
                                                     Action<int, IReadOnlyList<Ft8DecodeDto>>? onPass = null) =>
        ToDtos(FtxDecoder.Decode(audio, rate, isFt4, Table, passes,
            onPass is null ? null : (pass, found) => onPass(pass, ToDtos(found))));

    internal static IReadOnlyList<Ft8DecodeDto> ToDtos(IReadOnlyList<FtxDecode> decodes)
    {
        var list = new List<Ft8DecodeDto>(Math.Min(decodes.Count, MaxDecodesPerSlot));
        foreach (var d in decodes)
        {
            if (list.Count == MaxDecodesPerSlot) break;
            string text = d.Text.Trim();
            if (text.Length == 0) continue;
            list.Add(new Ft8DecodeDto
            {
                SnrDb = d.SnrDb,
                DtSec = Math.Round(d.DtSec, 2),
                FreqHz = (int)Math.Round(d.FreqHz),
                Score = d.Score,
                Text = text,
                WorkedBefore = false,        // plugin has no logbook — UI derives it
                Country = null,
            });
        }
        return list;
    }

    /// <summary>FT8: 79 symbols × 160 ms. FT4: 105 × 48 ms.</summary>
    public static int WaveSamples(bool isFt4, int sampleRate) => FtxSynth.WaveSamples(isFt4, sampleRate);

    /// <summary>
    /// Synthesize the full GFSK waveform for <paramref name="message"/>, full
    /// scale ±1.0 (the caller scales). Null with a readable
    /// <paramref name="error"/> when the message does not encode.
    /// </summary>
    public static float[]? Synth(string message, bool isFt4, float audioHz, int sampleRate, out string? error)
    {
        error = null;
        if (sampleRate <= 0)
        {
            error = "synth argument/buffer error";
            return null;
        }
        var wave = FtxSynth.Synth(message, isFt4, audioHz, sampleRate, Table, out var rc);
        if (wave is null) error = $"FT8 encoder rejected message '{message}' (grammar/pack error: {rc})";
        return wave;
    }
}
