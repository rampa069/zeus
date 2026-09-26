// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — decoder pipeline.
//
// Owns the RX ring, the slot boundary detector, and the decode worker. The
// decode itself runs OFF the audio thread: Push() only copies into a ring and
// returns. Decoding inline would starve the radio audio path.
//
// DecodeSlot() runs the FT8/FT4 decoder (Digital/Ft8, a C# port of ft8_lib).
// ZEUS_FT8_CAPTURE_DIR saves each slot's 12 kHz audio and its decodes, to grow
// the recorded golden corpus (TestData/ft8, docs/designs/ft8-managed-port.md).

using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Digital.Ft8;

namespace Zeus.Server.Hosting.Digital;

/// <summary>
/// Slot-aligned RX capture + decode dispatch.
/// </summary>
public sealed class DecoderPipeline : IDisposable
{
    /// <summary>ft8_lib works at 12 kHz; the ring holds SOURCE-rate audio and is
    /// resampled at decode time.</summary>
    public const int DecodeRate = 12_000;

    /// <summary>Max RX rate we size the ring for. The tap declares 48 kHz mono
    /// and AudioTapBridge delivers the pipeline's rate.</summary>
    private const int MaxSourceRate = 48_000;

    /// <summary>Ring span in seconds — must exceed one FT8 slot (15 s) with room
    /// for the worker to finish its snapshot before the producer laps it.</summary>
    private const int RingSeconds = 20;

    private readonly ClockService _clock;
    private readonly EventHub _events;
    private readonly object _sync = new();

    // 48 kHz * 20 s = 960k floats (~3.8 MB). Sized at the SOURCE rate: an
    // earlier draft sized it at DecodeRate (12 kHz) and so held only 5 s of
    // 48 kHz audio — silently truncating every 15 s slot.
    private readonly float[] _ring = new float[MaxSourceRate * RingSeconds];
    private int _write;
    private int _srcRate;
    private int _receiver;
    private long _currentSlot = -1;
    private DigitalMode _watchedMode = DigitalMode.Ft8;
    private bool _running;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    /// <param name="mode">
    /// The mode to decode, read fresh every cycle. FT4 halves the slot (7.5 s)
    /// and selects a different waveform in ft8_lib; this pipeline used to hard-
    /// wire FT8 in all three places — slot maths, the decode call and the
    /// published protocol — so selecting FT4 produced a receiver that listened
    /// on the wrong boundaries with the wrong demodulator and decoded nothing,
    /// for ever, with no error anywhere.
    /// </param>
    public DecoderPipeline(ClockService clock, EventHub events, Func<DigitalMode>? mode = null,
                           ILogger? log = null, Func<int>? passes = null)
    {
        _clock = clock;
        _events = events;
        _mode = mode ?? (static () => DigitalMode.Ft8);
        _log = log ?? NullLogger.Instance;
        _passes = passes ?? (static () => 1);
    }

    private readonly Func<int> _passes;

    private readonly ILogger _log;

    /// <summary>Where to save each slot for the golden corpus, if anywhere.</summary>
    internal string? CaptureDir { get; } = Environment.GetEnvironmentVariable("ZEUS_FT8_CAPTURE_DIR");

    private readonly Func<DigitalMode> _mode;

    /// <summary>The mode the next decode cycle will use.</summary>
    internal DigitalMode CurrentMode => _mode();

    /// <summary>True once a real decoder backend is linked — always, now that
    /// it is managed code. Reported in /status and as Ft8TxStatus.nativeAvailable.</summary>
    public bool Available => Ft8Managed.Available;

    /// <summary>Duration of the last slot's decode, all passes. Surfaced in
    /// /status so the timing budget is visible rather than mysterious.</summary>
    public double? LastLatencyMs { get; private set; }

    /// <summary>Time to the last slot's first batch — what the TX sequencer waits for.</summary>
    public double? FirstPassLatencyMs { get; private set; }

    public void Start()
    {
        lock (_sync)
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// AUDIO THREAD — realtime contract: no alloc, no lock, no IO, no throw.
    ///
    /// Deliberately LOCK-FREE. An earlier draft took a mutex that the decode
    /// worker also held while snapshotting the ring; that is the classic
    /// priority-inversion shape (a long consumer hold starving the audio
    /// producer) and it has bitten this codebase before. Single producer, single
    /// consumer: we write samples then publish the index with a release store.
    /// The consumer takes the last slot's worth of samples ending at that index —
    /// data it reads is ~15 s old and cannot be overwritten before it finishes,
    /// because the ring holds 20 s.
    /// </summary>
    public void Push(ReadOnlySpan<float> samples, int sampleRate, int receiver)
    {
        var ring = _ring;
        int w = _write;                       // only this thread writes _write
        for (int i = 0; i < samples.Length; i++)
        {
            ring[w] = samples[i];
            if (++w == ring.Length) w = 0;
        }
        Volatile.Write(ref _srcRate, sampleRate);
        Volatile.Write(ref _receiver, receiver);
        Volatile.Write(ref _write, w);        // release: publish after the samples
    }

    /// <summary>
    /// Slot watcher. Wakes shortly after each boundary, snapshots the slot that
    /// just ended, and dispatches a decode.
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);

                double now = _clock.UtcNowMs;

                // Mode is read every cycle: the operator can switch FT8<->FT4
                // while the decoder runs, and the slot grid changes under us
                // when they do. Re-baseline on the change instead of decoding
                // one window straddling both grids.
                DigitalMode mode = _mode();
                if (mode != _watchedMode)
                {
                    _watchedMode = mode;
                    _currentSlot = -1;
                }

                long slot = SlotClock.SlotIndex(now, mode);
                if (slot == _currentSlot) continue;

                long ended = slot - 1;
                _currentSlot = slot;
                if (ended < 0) continue;

                float[] audio;
                int rate, rx;
                // Lock-free snapshot: acquire the published index, then copy.
                // We never block the audio thread to read.
                int w = Volatile.Read(ref _write);
                rate = Volatile.Read(ref _srcRate);
                rx = Volatile.Read(ref _receiver);
                if (rate <= 0) continue;             // no RX audio yet

                // Hand the decoder EXACTLY the slot that just ended, aligned to
                // its boundary.
                //
                // An earlier draft passed the whole 20 s ring. ft8_lib's monitor
                // starts at sample 0 and fills one slot of blocks, so it analysed
                // -5 s..+10 s relative to the slot start — a 5 s misalignment
                // against a protocol that tolerates roughly +/-2.5 s of dt. The
                // result was frames arriving on time with decodes:[] forever, on
                // any band. Slot maths that is "close enough" is not close enough.
                //
                // `w` is the write head, i.e. now. We woke up to 100 ms after the
                // boundary, so step back that far to find the boundary in the
                // ring, then take the preceding slot.
                double msSinceBoundary = now - SlotClock.SlotStartMs(slot, mode);
                int samplesSinceBoundary = (int)(msSinceBoundary * rate / 1000.0);
                int slotSamples = SlotClock.SlotMs(mode) * rate / 1000;

                int end = w - samplesSinceBoundary;   // ring index of the boundary
                audio = Snapshot(end, slotSamples);
                if (audio.Length == 0) continue;

                var sw = Stopwatch.StartNew();
                IReadOnlyList<Ft8DecodeDto> decodes;
                float[]? audio12k = null;
                long slotStartMs = (long)SlotClock.SlotStartMs(ended, mode);
                bool firstPublished = false;

                // Each pass is published as soon as it is done. Pass 1 comes
                // first and always — even when empty: the store keys off slot
                // boundaries, and the TX sequencer acts the moment it lands, so
                // later passes must never hold it back.
                void Publish(int pass, IReadOnlyList<Ft8DecodeDto> found)
                {
                    _events.PublishFt8Decode(new Ft8DecodeBatch
                    {
                        Receiver = rx,
                        SlotStartUnixMs = slotStartMs,
                        Protocol = mode == DigitalMode.Ft4 ? "FT4" : "FT8",
                        Decodes = found,
                        Pass = pass,
                    });
                    if (pass == 1)
                    {
                        firstPublished = true;
                        FirstPassLatencyMs = sw.Elapsed.TotalMilliseconds;
                    }
                }

                try
                {
                    (decodes, audio12k) = DecodeSlot(audio, rate, mode, _passes(), Publish);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "ft8: decode failed for slot {Slot}", slotStartMs);
                    decodes = Array.Empty<Ft8DecodeDto>();
                }
                if (!firstPublished) Publish(1, decodes);
                sw.Stop();
                LastLatencyMs = sw.Elapsed.TotalMilliseconds;

                // Only after publishing: the TX sequencer acts on each slot's
                // first batch as it lands, so nothing optional may sit between
                // the decode and the publish.
                if (audio12k is not null && CaptureDir is not null)
                {
                    var (a, d, md, s) = (audio12k, decodes, mode, slotStartMs);
                    _ = Task.Run(() => Capture(s, md, a, d));
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    // ---- SEAM ---------------------------------------------------------------

    /// <summary>
    /// Copy <paramref name="count"/> samples ending at ring index
    /// <paramref name="end"/>, oldest-first. Runs on the worker, never the audio
    /// thread. Returns empty if the window is larger than the ring.
    /// </summary>
    private float[] Snapshot(int end, int count)
    {
        var ring = _ring;
        if (count <= 0 || count > ring.Length) return Array.Empty<float>();

        // Normalise `end` into [0, ring.Length) — it can go negative when the
        // boundary sits before the current write head's wrap point.
        end %= ring.Length;
        if (end < 0) end += ring.Length;

        int start = end - count;
        if (start < 0) start += ring.Length;

        var outBuf = new float[count];
        if (start + count <= ring.Length)
        {
            Array.Copy(ring, start, outBuf, 0, count);
        }
        else
        {
            int first = ring.Length - start;
            Array.Copy(ring, start, outBuf, 0, first);
            Array.Copy(ring, 0, outBuf, first, count - first);
        }
        return outBuf;
    }

    /// <summary>
    /// Decode one slot with the managed decoder, audio already aligned to the
    /// slot boundary on the disciplined clock (so dtSec is measured against it).
    /// Returns every decode of every pass (each also handed to
    /// <paramref name="onPass"/> as its pass completes) and the 12 kHz audio for
    /// the capture.
    /// </summary>
    private static (IReadOnlyList<Ft8DecodeDto>, float[]) DecodeSlot(float[] audio, int rate, DigitalMode mode,
        int passes, Action<int, IReadOnlyList<Ft8DecodeDto>> onPass)
    {
        // Resample once and decode the 12 kHz copy: the decoder would resample
        // to exactly this, and it is what a capture must hold to replay the slot.
        float[] audio12k = FtxDecoder.ResampleTo12k(audio, rate);
        var all = Ft8Managed.Decode(audio12k, FtxDecoder.DecodeRate, mode == DigitalMode.Ft4, passes, onPass);
        return (all, audio12k);
    }

    /// <summary>Save one slot for the golden corpus: &lt;slotStartMs&gt;_&lt;FT8|FT4&gt;.f32
    /// (12 kHz mono float32 LE) and a .txt of its decodes, one per line: text,
    /// freqHz, dtSec, snrDb, score (the columns of TestData/ft8/*.native.tsv).</summary>
    private void Capture(long slotStartMs, DigitalMode mode, float[] audio12k, IReadOnlyList<Ft8DecodeDto> decodes)
    {
        try
        {
            Directory.CreateDirectory(CaptureDir!);
            string stem = Path.Combine(CaptureDir!, $"{slotStartMs}_{(mode == DigitalMode.Ft4 ? "FT4" : "FT8")}");
            var bytes = new byte[audio12k.Length * 4];
            for (int i = 0; i < audio12k.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), audio12k[i]);
            File.WriteAllBytes(stem + ".f32", bytes);

            File.WriteAllLines(stem + ".txt", decodes.Select(Line));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ft8: slot capture failed ({Dir})", CaptureDir);
        }

        static string Line(Ft8DecodeDto d) =>
            FormattableString.Invariant($"{d.Text}\t{d.FreqHz}\t{d.DtSec:0.00}\t{d.SnrDb}\t{d.Score}");
    }

    public void Dispose()
    {
        Stop();
        try { _worker?.Wait(2000); } catch { }
        _cts?.Dispose();
    }
}
