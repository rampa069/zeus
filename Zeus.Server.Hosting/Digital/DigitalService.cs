// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// Digital modes service (FT8/FT4) — IN CORE.
//
// WHY IN CORE, NOT A PLUGIN
// Upstream extracted the FT8/FT4/WSPR decoders out of core into the
// org.openhpsdr.digital plugin, distributed from a registry that no longer
// exists (raw.githubusercontent.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins
// → 404). A plugin that cannot be fetched, and must be hand-placed in the
// plugin root, is a worse deal than one that ships in the binary. So the
// decoder lives here and is published by `dotnet publish` like any other
// service — nothing to install, nothing to copy.
//
// The frontend is unchanged: it addresses the backend at
// /api/plugins/org.openhpsdr.digital/... and gates on GET /status returning
// 2xx (see zeus-web/src/api/digital-plugin.ts). Core simply serves those
// routes. resolveDigitalPluginId() falls back to the canonical id when the
// plugin list is empty, so nothing needs to be "installed" for the UI to bind.
//
// SCOPE: the QSO auto-sequence state machine is NOT here and must never be.
// It lives in zeus-web/src/dsp/ft8-sequencer.ts — pure, deterministic,
// unit-tested. The frontend runs it, stages a message, and this backend keys
// exactly that message. Upstream kept "a separate, BENCH-GATED backend port of
// this same table" (ft8-sequencer.ts header) — two implementations of one
// protocol, which is how they diverge and why a station could answer a CQ and
// never be replied to.
//
// TX: the modulator is Ft8KeyerService (same folder) — a hosted service that
// watches the SlotClock and keys the staged message at its boundary. It owns
// Transmitting/LastTxSlotMs below; this service only reports them.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
// DspPipelineService lives in namespace Zeus.Server (the project's root ns),
// not Zeus.Server.Hosting — the folder name and namespace differ here.
using Zeus.Server;

namespace Zeus.Server.Hosting.Digital;

/// <summary>
/// Owns the RX tap subscription, the decoder pipeline and the TX stage book.
/// Registered as a singleton + IHostedService; the endpoints resolve it from DI.
/// </summary>
public sealed class DigitalService : IHostedService, IDisposable
{
    public const string PluginId = "org.openhpsdr.digital";

    private readonly DspPipelineService _pipeline;
    private readonly ILogger<DigitalService> _log;

    public ClockService Clock { get; } = new();
    public EventHub Events { get; } = new();
    public TxStageBook Stages { get; } = new();
    public DecoderPipeline Decoder { get; }

    public bool Ft8Enabled { get; private set; }

    /// <summary>FT8/FT4 decode depth: passes, each after subtracting what the
    /// previous ones decoded (1 = a single pass). Set by /ft8/enable.</summary>
    public int Passes
    {
        get => _passes;
        set => _passes = Math.Clamp(value, 1, Ft8.FtxDecoder.MaxPasses);
    }
    private volatile int _passes = 1;
    public bool WsprEnabled { get; private set; }
    public bool Armed { get; private set; }
    public int AudioHz { get; set; } = 1500;
    public string Mode { get; set; } = "FT8";

    /// <summary>
    /// <see cref="Mode"/> as the enum the slot maths and the decoder need. FT4
    /// is not a variant of FT8 on the wire: half the slot (7.5 s) and a
    /// different waveform, so RX has to be told, not assumed.
    /// </summary>
    public DigitalMode ModeKind => ModeKindOf(Mode);

    /// <summary>The mode string as the enum, in one place so RX and TX cannot
    /// disagree about what "FT4" means.</summary>
    public static DigitalMode ModeKindOf(string? mode) =>
        string.Equals(mode, "FT4", StringComparison.OrdinalIgnoreCase)
            ? DigitalMode.Ft4 : DigitalMode.Ft8;
    public string? Callsign { get; set; }
    public string? Grid { get; set; }

    // --- live TX state, owned by Ft8KeyerService --------------------------
    private volatile bool _transmitting;

    /// <summary>True while the keyer is streaming a signal (MOX held).</summary>
    public bool Transmitting
    {
        get => _transmitting;
        internal set => _transmitting = value;
    }

    /// <summary>Slot-start UTC ms of the most recent keyed transmission.</summary>
    public double? LastTxSlotMs { get; internal set; }

    private volatile TxStage? _keyedStage;

    /// <summary>
    /// The stage the keyer is actually putting on the air, held for the whole
    /// transmission. The frontend re-stages the NEXT message roughly a second
    /// into the current one (decodes land, the sequencer steps), so reporting
    /// Stages.Peek() while transmitting shows the operator the message AFTER the
    /// one their radio is sending.
    /// </summary>
    public TxStage? KeyedStage
    {
        get => _keyedStage;
        internal set => _keyedStage = value;
    }

    public DigitalService(DspPipelineService pipeline, ILogger<DigitalService> log)
    {
        _pipeline = pipeline;
        _log = log;
        Decoder = new DecoderPipeline(Clock, Events, () => ModeKind, log, () => Passes);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _pipeline.RxAudioAvailable += OnRxAudio;
        _log.LogInformation(
            "digital: FT8 backend in core (managed decoder{Capture}, clock={Clock})",
            Decoder.CaptureDir is null ? "" : $", capturing to {Decoder.CaptureDir}",
            Clock.Status.Source);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _pipeline.RxAudioAvailable -= OnRxAudio;
        Decoder.Stop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// RX AUDIO THREAD. Copy into the decoder's lock-free ring and return.
    /// Must not allocate, lock, or block — a long hold here starves the radio
    /// audio path (this codebase has been bitten by exactly that before).
    /// </summary>
    private void OnRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        if (!Ft8Enabled && !WsprEnabled) return;
        Decoder.Push(samples.Span, sampleRateHz, receiver);
    }

    public void EnableFt8(bool on)
    {
        Ft8Enabled = on;
        if (on) Decoder.Start(); else Decoder.Stop();
        _log.LogInformation("digital: ft8 {State}", on ? "enabled" : "disabled");
    }

    public void EnableWspr(bool on) => WsprEnabled = on;

    /// <summary>Arm/disarm the keyer. Refuses to arm on an undisciplined clock.</summary>
    public bool TryArm(bool enabled, out string? error)
    {
        error = null;
        if (enabled && !Clock.SafeToTransmit)
        {
            var c = Clock.Status;
            error = $"UTC offset {c.OffsetMs:F0} ms (source: {c.Source}). " +
                    "FT8 needs the clock within ±1.5 s before transmitting.";
            return false;
        }
        // Log every edge. The keyer logs what it transmits, but an arm that
        // never happened, or a disarm nobody asked for, left no trace at all —
        // which made "it stopped transmitting mid-QSO" impossible to place
        // between the operator, the UI and the sequencer.
        if (Armed != enabled)
            _log.LogInformation("digital: keyer {State}", enabled ? "ARMED" : "disarmed");
        Armed = enabled;
        if (!enabled) Stages.Clear();
        Events.PublishTxStatus(BuildTxStatus());
        return true;
    }

    public void Halt()
    {
        if (Armed) _log.LogInformation("digital: keyer halted");
        // Armed=false aborts an in-flight keyer pump within one audio block;
        // the keyer's finally then drops MOX and publishes the idle status.
        Armed = false;
        Stages.Clear();
        Events.PublishTxStatus(BuildTxStatus());
    }

    /// <summary>
    /// Which stage the TX status reports. While transmitting it is the one ON
    /// THE AIR; otherwise the staged one. The frontend re-stages the next
    /// message about a second into the current transmission (decodes land, the
    /// sequencer steps), so reporting the staged one throughout showed the
    /// operator the message AFTER the one their radio was sending.
    /// </summary>
    internal static TxStage? ReportedStage(bool transmitting, TxStage? keyed, TxStage? staged) =>
        (transmitting ? keyed : null) ?? staged;

    public Ft8TxStatus BuildTxStatus()
    {
        var s = ReportedStage(Transmitting, KeyedStage, Stages.Peek());
        return new Ft8TxStatus
        {
            Armed = Armed,
            Transmitting = Transmitting,
            Mode = Mode,
            Message = s?.Message,
            AudioHz = AudioHz,
            Slot = s?.Slot ?? "",
            WatchdogSecsRemaining = 0,
            LastTxSlotMs = LastTxSlotMs is { } lastMs ? (long?)Math.Round(lastMs) : null,
            NativeAvailable = Ft8Managed.Available,
        };
    }

    public void Dispose()
    {
        Decoder.Dispose();
        Clock.Dispose();
    }
}
