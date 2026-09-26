// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus Digital plugin — wire DTOs.
//
// These mirror the frontend types EXACTLY. Field names are camelCase on the wire
// and are consumed unchanged by the existing stores:
//
//   Ft8DecodeDto / Ft8DecodeBatch  → zeus-web/src/state/ft8-store.ts   (`ft8decode`)
//   Ft8TxStatus                    → zeus-web/src/state/ft8-tx-store.ts (`txstatus`)
//   TxStageRequest                 → posted by dsp/ft8-tx-controller.ts postStage()
//
// Do not "improve" these shapes: the UI is already written and tested against them.

using System.Text.Json.Serialization;

namespace Zeus.Server.Hosting.Digital;

/// <summary>One decoded message line. Mirrors Ft8DecodeDto.</summary>
public sealed record Ft8DecodeDto
{
    [JsonPropertyName("snrDb")] public required int SnrDb { get; init; }
    [JsonPropertyName("dtSec")] public required double DtSec { get; init; }
    [JsonPropertyName("freqHz")] public required int FreqHz { get; init; }
    [JsonPropertyName("score")] public int Score { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }

    /// <summary>Always false — the plugin has no logbook access. The decode table
    /// derives worked-before at render time from digital-worked-store.</summary>
    [JsonPropertyName("workedBefore")] public bool WorkedBefore { get; init; }

    /// <summary>Abbreviated DXCC entity from the sender's callsign prefix
    /// (FT8 never transmits country). null when unknown.</summary>
    [JsonPropertyName("country")] public string? Country { get; init; }
}

/// <summary>A completed slot's decodes for one receiver — the `ft8decode` payload.</summary>
public sealed record Ft8DecodeBatch
{
    [JsonPropertyName("receiver")] public required int Receiver { get; init; }
    [JsonPropertyName("slotStartUnixMs")] public required long SlotStartUnixMs { get; init; }
    [JsonPropertyName("protocol")] public required string Protocol { get; init; }   // "FT8" | "FT4"
    [JsonPropertyName("decodes")] public required IReadOnlyList<Ft8DecodeDto> Decodes { get; init; }
    /// <summary>Decoding pass. Pass 1 is the slot's first batch and always
    /// comes; with multi-pass decoding, later passes follow for the same slot
    /// carrying only the decodes they added.</summary>
    [JsonPropertyName("pass")] public int Pass { get; init; } = 1;
}

/// <summary>The `txstatus` payload. BACKEND-AUTHORITATIVE — Ft8TxControl lights its
/// arm/TX lamps from this, not from local optimism. Emit on every edge.</summary>
public sealed record Ft8TxStatus
{
    [JsonPropertyName("armed")] public bool Armed { get; init; }
    [JsonPropertyName("transmitting")] public bool Transmitting { get; init; }
    [JsonPropertyName("mode")] public string Mode { get; init; } = "FT8";
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("audioHz")] public int AudioHz { get; init; } = 1500;
    [JsonPropertyName("slot")] public string Slot { get; init; } = "";
    [JsonPropertyName("watchdogSecsRemaining")] public int WatchdogSecsRemaining { get; init; }
    [JsonPropertyName("lastTxSlotMs")] public long? LastTxSlotMs { get; init; }
    [JsonPropertyName("nativeAvailable")] public bool NativeAvailable { get; init; }
}

/// <summary>
/// POST /ft8/enable body. The workspace has always sent this; the handler used
/// to ignore it, which is how FT4 ended up decoding nothing. `passes` sets the
/// decode depth (1-4: passes with subtraction). `receiver` is accepted and
/// unused — the receiver comes from the audio tap.
/// </summary>
public sealed record Ft8EnableRequest
{
    [JsonPropertyName("protocol")] public string? Protocol { get; init; }
    [JsonPropertyName("receiver")] public int? Receiver { get; init; }
    [JsonPropertyName("passes")] public int? Passes { get; init; }
}

/// <summary>POST /ft8/tx — the stage. `message` is keyed VERBATIM.</summary>
public sealed record TxStageRequest
{
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("audioHz")] public int? AudioHz { get; init; }
    [JsonPropertyName("slot")] public string? Slot { get; init; }
    [JsonPropertyName("mode")] public string? Mode { get; init; }
}

/// <summary>POST /wspr/enable body.</summary>
public sealed record CwSkimEnableRequest(int? Receiver);

/// <summary>POST /sstv/enable body.</summary>
public sealed record SstvEnableRequest(int? Receiver);

public sealed record WsprEnableRequest
{
    [JsonPropertyName("receiver")] public int? Receiver { get; init; }
    [JsonPropertyName("dialFreqMhz")] public double? DialFreqMhz { get; init; }
}

/// <summary>POST /wspr/tx/settings body (WsprTxControl.tsx contract).</summary>
public sealed record WsprTxSettingsRequest
{
    [JsonPropertyName("call")] public string? Call { get; init; }
    [JsonPropertyName("grid4")] public string? Grid4 { get; init; }
    [JsonPropertyName("dBm")] public int? DBm { get; init; }
    [JsonPropertyName("audioHz")] public int? AudioHz { get; init; }
    [JsonPropertyName("txPercent")] public double? TxPercent { get; init; }
}

public sealed record ArmRequest
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
}

public sealed record IdentityRequest
{
    [JsonPropertyName("call")] public string? Call { get; init; }
    [JsonPropertyName("grid")] public string? Grid { get; init; }
}

public sealed record WsjtxLiveRequest
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("host")] public string? Host { get; init; }
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("multicast")] public bool Multicast { get; init; }
    [JsonPropertyName("instanceId")] public string? InstanceId { get; init; }
}

/// <summary>GET /status — the liveness probe / mode gate. 2xx unlocks the UI.</summary>
public sealed record DigitalStatus
{
    [JsonPropertyName("ok")] public bool Ok { get; init; } = true;
    [JsonPropertyName("version")] public string Version { get; init; } = "0.1.0";
    [JsonPropertyName("ft8Enabled")] public bool Ft8Enabled { get; init; }
    [JsonPropertyName("wsprEnabled")] public bool WsprEnabled { get; init; }
    [JsonPropertyName("decoderAvailable")] public bool DecoderAvailable { get; init; }

    /// <summary>Last decode pass duration — makes the timing budget visible instead
    /// of mysterious. Null until the first pass completes.</summary>
    [JsonPropertyName("decodeLatencyMs")] public double? DecodeLatencyMs { get; init; }
    /// <summary>Time to the slot's first decode batch (pass 1) — what the TX
    /// sequencer waits for; decodeLatencyMs covers every pass.</summary>
    [JsonPropertyName("firstPassLatencyMs")] public double? FirstPassLatencyMs { get; init; }

    [JsonPropertyName("clock")] public ClockStatusDto? Clock { get; init; }

    /// <summary>Echo of the last /config/identity push — the frontend ignores
    /// these, but they make the identity round-trip visible when debugging.</summary>
    [JsonPropertyName("call")] public string? Call { get; init; }
    [JsonPropertyName("grid")] public string? Grid { get; init; }
}

public sealed record ClockStatusDto
{
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("offsetMs")] public double OffsetMs { get; init; }
    [JsonPropertyName("driftPpm")] public double DriftPpm { get; init; }
    [JsonPropertyName("syncedAtUnixMs")] public long? SyncedAtUnixMs { get; init; }
    [JsonPropertyName("healthy")] public bool Healthy { get; init; }

    public static ClockStatusDto From(ClockStatus s) => new()
    {
        Source = s.Source,
        OffsetMs = double.IsNaN(s.OffsetMs) ? 0 : s.OffsetMs,
        DriftPpm = double.IsNaN(s.DriftPpm) ? 0 : s.DriftPpm,
        SyncedAtUnixMs = s.SyncedAtUnixMs,
        Healthy = s.Healthy,
    };
}
