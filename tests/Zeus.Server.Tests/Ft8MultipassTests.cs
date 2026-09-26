// SPDX-License-Identifier: GPL-2.0-or-later
//
// Multi-pass FT8/FT4 decoding (zeus-8d4i): each pass after the first
// subtracts what was decoded (FtxSubtract) and searches again, so a signal
// hidden under a stronger one comes through. One pass stays exactly the
// native decoder (Ft8GoldenTests).

using System.Buffers.Binary;
using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Ft8;

namespace Zeus.Server.Tests;

public sealed class Ft8MultipassTests
{
    private const int Rate = 12_000;

    /// <summary>A 12 kHz slot in Gaussian noise (rms 0.05) with transmissions added.</summary>
    private static float[] Slot(bool isFt4, params (string Msg, float Hz, float Dt, float Amp)[] txs)
    {
        int n = (int)((isFt4 ? 7.5 : 15.0) * Rate);
        var a = new float[n];
        var rng = new Random(5);
        for (int i = 0; i + 1 < n; i += 2)
        {
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble(), r = Math.Sqrt(-2 * Math.Log(u1)) * 0.05;
            a[i] = (float)(r * Math.Cos(2 * Math.PI * u2));
            a[i + 1] = (float)(r * Math.Sin(2 * Math.PI * u2));
        }
        foreach (var (msg, hz, dt, amp) in txs)
        {
            var w = FtxSynth.Synth(msg, isFt4, hz, Rate, null, out var rc)!;
            Assert.Equal(FtxMessageRc.Ok, rc);
            int st = (int)(dt * Rate);
            for (int i = 0; i < w.Length && st + i < n; i++) a[st + i] += amp * w[i];
        }
        return a;
    }

    // A station 26-20 dB weaker, 6 Hz (FT8) / 41 Hz (FT4) from a strong one and
    // in the same slot: one pass decodes only the strong one; the second pass,
    // on what is left after subtracting it, decodes the weak one too.
    [Theory]
    [InlineData(false, 6f, 0.05f)]
    [InlineData(false, 20f, 0.1f)]
    [InlineData(true, 41f, 0.1f)]
    public void SecondPass_FindsTheSignalUnderTheStrongOne(bool isFt4, float offsetHz, float weak)
    {
        var a = Slot(isFt4, ("CQ EA5IUE IM76", 1000f, 0.5f, 1.0f), ("EA5IUE W1AW -15", 1000f + offsetHz, 0.5f, weak));

        var one = FtxDecoder.Decode(a, Rate, isFt4, null, passes: 1).Select(d => d.Text).ToList();
        var two = FtxDecoder.Decode(a, Rate, isFt4, null, passes: 2).Select(d => d.Text).ToList();

        Assert.Equal(["CQ EA5IUE IM76"], one);
        Assert.Contains("CQ EA5IUE IM76", two);
        Assert.Contains("EA5IUE W1AW -15", two);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Subtraction_RemovesMostOfADecodedSignal(bool isFt4)
    {
        var a = Slot(isFt4, ("CQ EA5IUE IM76", 1234.5f, 0.37f, 0.5f));
        // (FT4 can list the same message twice ~10 Hz apart — zeus-sk6o.)
        var d = FtxDecoder.Decode(a, Rate, isFt4, null).First(x => x.Text == "CQ EA5IUE IM76");
        var p = new byte[FtxMessage.PayloadBytes];
        Assert.Equal(FtxMessageRc.Ok, FtxMessage.Encode(d.Text, null, p));

        var b = (float[])a.Clone();
        FtxSubtract.Subtract(b, isFt4, p, d.FreqHz, d.DtSec);

        double before = 0, after = 0;
        for (int i = 0; i < a.Length; i++) { before += a[i] * a[i]; after += b[i] * b[i]; }
        double noise = a.Length * 0.05 * 0.05;
        // The signal is ~15 dB above the noise over the slot; what is left must
        // be within 1 dB of the noise alone.
        Assert.True(before > 20 * noise);
        Assert.InRange(10 * Math.Log10(after / noise), -0.5, 1.0);
        Assert.Empty(FtxDecoder.Decode(b, Rate, isFt4, null));
    }

    [Fact]
    public void OnPass_ReportsPassOneFirst_ThenOnlyWhatLaterPassesAdd()
    {
        var a = Slot(false, ("CQ EA5IUE IM76", 1000f, 0.5f, 1.0f), ("EA5IUE W1AW -15", 1006f, 0.5f, 0.05f));
        var seen = new List<(int Pass, string[] Texts)>();
        var all = FtxDecoder.Decode(a, Rate, false, null, passes: 3,
            onPass: (pass, found) => seen.Add((pass, found.Select(d => d.Text).ToArray())));

        Assert.Equal(2, seen.Count);                       // pass 3 found nothing: not reported
        Assert.Equal(1, seen[0].Pass);
        Assert.Equal(["CQ EA5IUE IM76"], seen[0].Texts);
        Assert.Equal(2, seen[1].Pass);
        Assert.Equal(["EA5IUE W1AW -15"], seen[1].Texts);
        Assert.Equal(["CQ EA5IUE IM76", "EA5IUE W1AW -15"], all.Select(d => d.Text));
    }

    [Fact]
    public void OnPass_ReportsAnEmptyFirstPass()
    {
        var seen = new List<int>();
        FtxDecoder.Decode(Slot(false), Rate, false, null, passes: 3, onPass: (pass, _) => seen.Add(pass));
        Assert.Equal([1], seen);
    }

    // On the recorded 20 m slots, three passes keep every first-pass decode and
    // add more (busy FT8 slots gain the most).
    [Fact]
    public void RecordedSlots_ThreePassesKeepEverythingAndFindMore()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "TestData", "ft8");
        int one = 0, three = 0;
        foreach (var f32 in Directory.GetFiles(dir, "*.f32").Order())
        {
            var bytes = File.ReadAllBytes(f32);
            var audio = new float[bytes.Length / 4];
            for (int i = 0; i < audio.Length; i++) audio[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
            bool isFt4 = f32.EndsWith("_FT4.f32", StringComparison.Ordinal);

            var p1 = FtxDecoder.Decode(audio, Rate, isFt4, new FtxCallsignTable(), 1).Select(d => d.Text).ToList();
            var p3 = FtxDecoder.Decode(audio, Rate, isFt4, new FtxCallsignTable(), 3).Select(d => d.Text).ToList();
            Assert.Empty(p1.Except(p3));
            one += p1.Count;
            three += p3.Count;
        }
        Assert.True(three >= one * 1.15, $"1 pass {one}, 3 passes {three}");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(9, FtxDecoder.MaxPasses)]
    public void Passes_AreClamped(int asked, int expected)
    {
        var d = new DigitalService(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<DigitalService>.Instance)
        {
            Passes = asked,
        };
        Assert.Equal(expected, d.Passes);
    }
}
