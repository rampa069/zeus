# FT8/FT4 in managed C# — evaluation (zeus-wi0p)

**Decision: GO**, with the same method as the WSPR port
(`docs/designs/wspr-managed-port.md`). Port the vendored **ft8_lib** and
the `zeus_ft8.c` shim (`native/ft8`) to C#. Keep the native library as the
oracle until the managed path matches it on a golden corpus, then drop it.

**Status: done** on `feat/ft8-managed`. The port lives in
`Zeus.Server.Hosting/Digital/Ft8` (with `Ft8Managed` as the service facade).
It matched native exactly:
- the encoder on 106 messages (return code, payload, FT8 and FT4 tones) and
  the unpacker on 2000 random payloads;
- the decoder on 5 synthesized slots and, live on the HL2 in shadow mode, on
  79 slots (27 FT8, 52 FT4; 754 decodes), with every transmitted waveform
  checked too.

Eight of those slots (4 FT8, 4 FT4, 4.4 MB) and the native output are frozen
in `tests/Zeus.Server.Tests/TestData/ft8`. `native/ft8`, its CI steps and
the four per-RID libraries are removed. Release timing is about 31 ms per FT8
slot and 24 ms per FT4 slot on Apple Silicon. The encoder quirks below are
still reproduced; fixing them is follow-up work.

## Why

The case is weaker than for WSPR, because `zeus_ft8.dll` does ship for
win-x64. What a managed port still buys:

- **No per-RID native builds.** `libzeus_ft8` is built for linux-x64,
  linux-arm64, osx-arm64 and win-x64. The win-arm64 directory is empty, so
  Windows on ARM has no FT8 or FT4. A managed port removes the CI legs and
  the committed binaries.
- **Room to decode better.** Today the decoder makes a **single pass**. The
  UI's "passes" setting reaches the backend (`Dtos.cs`) and is ignored. There
  is no subtraction of decoded signals and no a-priori decoding. Those are
  WSJT-X's main sensitivity gains, and they are much easier to add and test in
  C# than in a vendored C tree behind P/Invoke.
- **Small native defects go away:**
  - `estimate_snr_db()` keeps its two spectrum buffers in `static` arrays, so
    it is not reentrant. This is harmless while one worker decodes.
  - 48 kHz → 12 kHz is linear interpolation at an exact ratio of 4, i.e.
    plain decimation with **no anti-alias filter**. It relies on the DIGU RX
    filter to have removed everything above 6 kHz.
  - The encoder has C-string quirks (listed below) that silently change
    what is sent.

## What there is to port

| File | Lines | Content |
|---|---|---|
| `ft8/message.c` | 1155 | Pack and unpack of 77-bit messages (types 0.0, 0.5, 1, 2, 4), callsign hashing |
| `ft8/decode.c` | 591 | Candidate search (Costas sync score), soft bits, LDPC + CRC check |
| `ft8/constants.c` | 391 | Costas, Gray, XOR sequence, LDPC generator and parity tables (data) |
| `ft8/text.c` | 303 | Character tables and token helpers |
| `common/monitor.c` | 263 | Windowed FFT waterfall with time/frequency oversampling |
| `ft8/ldpc.c` | 251 | Belief-propagation LDPC decoder |
| `ft8/encode.c` | 195 | CRC + LDPC (174,91) encode, tone mapping for FT8 and FT4 |
| `ft8/crc.c` | 63 | CRC-14 |
| `zeus_ft8.c` | 571 | Resample, SNR estimate, GFSK synth, locked callsign hash table |
| `fft/kiss_fft*` | ~750 | Replaced by a managed FFT |

About 3.2k lines of C to port, roughly 2–2.5k lines of C#.

## How: keep native as the oracle

1. **Encoder first.** Pack, CRC, LDPC encode and tone map in C#. A small
   C harness built from the same vendored sources dumps, for a table of
   messages, the return code, the 77-bit payload and the tones for FT8 and FT4.
   Its output is frozen into `TestData/ft8/encoder-native.tsv`, and the managed
   encoder must match it exactly.
2. **Synth.** The GFSK synthesis produces the same waveform within float
   tolerance. As a hard check, the native decoder must decode the managed
   waveform.
3. **Unpack + LDPC decoder.** Both are integer or deterministic code. Feed the
   same inputs and require the same outputs.
4. **Decoder.** Monitor, candidates, soft demod, LDPC, SNR. The golden tests
   compare spot lists on synthesized slots and on **real recorded slots**
   (FT8 and FT4, busy bands), captured the way WSPR's were. Messages and
   frequencies must match; SNR and dt must agree within small tolerances.
5. **Switch** `DecoderPipeline` and `Ft8KeyerService` to managed code, run a
   live shadow comparison, freeze the oracle, then delete `native/ft8`, its CI
   steps and the per-RID libraries.
6. **Afterwards (zeus-8d4i, done):** multi-pass decoding with subtraction,
   honouring `passes`. `FtxSubtract` rebuilds each decoded signal from its
   payload, refines start and frequency (baseband at 32 samples per symbol,
   then full rate), estimates its complex amplitude with a normalised
   triangle low-pass of s·conj(c) and subtracts it — about 30 dB of the signal
   goes. Each pass is published as it completes (`Ft8DecodeBatch.pass`), so
   the TX sequencer still acts on pass 1. Three passes: +38 % decodes on 27
   busy FT8 slots, +7 % on 52 FT4 slots; ~470 ms per FT8 slot on Apple Silicon
   in Release (pass 1 alone ~75 ms).

Exactness caveats, as for WSPR: `cosf`/`erff`/`sinf` versus `MathF`, and float
accumulation order, can change **borderline** weak decodes. The bar is the
same spots on the corpus. The pack, CRC, LDPC and hash paths are integer code
and must be exact.

## Encoder quirks found so far (reproduced first, fixed later)

- `copy_token` never reports an over-long token. It truncates to the buffer
  (11 chars for a call, 19 for the third field), so the "token too long"
  checks in `ftx_message_encode` never fire.
- `packgrid` turns any third field it does not recognise into a report of
  **+00**. For example, `K1ABC W9XYZ HELLO` is sent as `K1ABC W9XYZ +00`
  instead of falling back to free text.
- `ftx_message_encode_nonstd` tests `call_de[len_call_to - 1]` for the closing
  `>` (it should use `len_call_de`).
- A bracketed hashed call cannot be encoded at all: `<` and `>` are not in the
  hash alphabet, so `<W9XYZ> PJ4/K1ABC RR73` fails both the standard and the
  type-4 packer and is refused (too long for free text).
- Lower-case input is refused (`cq k1abc fn42`), and a leading space makes a
  standard message fall back to free text.

## Effort

| Work | Sessions |
|---|---|
| Encoder + synth | ½ |
| Unpack + LDPC decode | ½ |
| Monitor + candidates + SNR | 1 |
| Corpus + golden tests + switch-over | 1 |

Roughly 3–4 focused sessions.

## Consequences

- **Windows on ARM gets FT8/FT4.**
- **Build:** `native/ft8`, its CI steps and four committed binaries go away.
  FFTW is not involved (ft8_lib uses its own kiss_fft).
- **Licence:** ft8_lib is MIT (keep `LICENSE.ft8_lib` next to the port). The
  shim and the port are GPL-2.0-or-later like the rest of Zeus.
