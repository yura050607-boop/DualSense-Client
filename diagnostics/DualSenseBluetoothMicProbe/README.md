# DualSense Bluetooth microphone probe

This is an explicit, bounded feasibility diagnostic for the standard Sony DualSense
built-in microphone over Bluetooth. It is intentionally separate from the GUI and is
never started by normal application paths.

```text
DualSenseBluetoothMicProbe.exe --help
DualSenseBluetoothMicProbe.exe --dry-run --duration-ms 3000
DualSenseBluetoothMicProbe.exe --self-test
DualSenseBluetoothMicProbe.exe --run --duration-ms 3000
DualSenseBluetoothMicProbe.exe --list-capture
DualSenseBluetoothMicProbe.exe --capture --endpoint-id "<exact-id-from-list>" --duration-ms 3000
```

`--run` requires exactly one connected standard DualSense Bluetooth HID candidate. It
rejects USB, known virtual identities, ambiguity, and known competing controller/audio
processes. The run enables the duplex session only for the requested duration, validates
the fixed-size input envelope, CRC32, and Opus decode, and prints counters only. It does
not publish a virtual gamepad or save raw reports, PCM, or speech. Cancellation, read
failure, and disconnects attempt both duplex disable and neutral output restoration.
Because the probe cannot safely snapshot every pre-existing controller field, neutral
restoration is an explicit safe reset rather than an exact restoration claim.
An accepted Opus frame proves packet integrity and decoder compatibility only; it does
not prove that the packet contains speech.

`--list-capture` is read-only and prints the current Windows capture endpoint identity,
friendly name, default flag, and advertised formats. `--capture` requires one exact
endpoint ID from that list, re-enumerates it with an ordinal identity match, opens
shared WASAPI for at most 30 seconds, and reports only the negotiated format, frame
count/duration, per-channel normalized RMS/peak/clipping/DC, L/R correlation, and
three whole-window spectral ratios. It never falls back to a default or friendly-name
match and never writes a WAV, raw PCM, packet, waveform, or network artifact.

The `--run` summary includes the same aggregate signal line for decoded Bluetooth
PCM. The BT line and Windows capture line intentionally use the same normalized
conventions, but aggregate similarity is not a speech-quality or parity claim.

The parser follows the narrow public lead in
[`LinuxAudio4Dualsense5`'s `ds5_membrane_sink.c`](https://raw.githubusercontent.com/GeorgLegato/LinuxAudio4Dualsense5/main/src/ds5_membrane_sink.c)
(buffer cap 256, report `0x31`, Opus marker `0xD4` at offset 3), but treats the marker
as a candidate only. CRC and decoder validation are mandatory before a frame is counted.
