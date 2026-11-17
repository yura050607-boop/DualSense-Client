using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using DualSenseClient.Controllers.DualSense.Audio;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace DualSenseBluetoothMicProbe;

/// <summary>
/// Exact-ID, shared-mode Windows capture diagnostic. It never selects a default
/// device or falls back to a friendly name.
/// </summary>
internal static class WindowsCaptureSignalProbe
{
    private const int MaximumDurationMilliseconds = 30000;

    /// <summary>
    /// Lists current capture endpoint IDs and negotiated-capability metadata. The
    /// caller must use one exact ID from this list for a later capture command.
    /// </summary>
    public static int ListCaptureEndpoints()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("refused: Windows capture diagnostics require Windows WASAPI");
            return 2;
        }

        try
        {
            using MiniAudioEngine engine = new MiniAudioEngine();
            engine.UpdateAudioDevicesInfo();
            DeviceInfo[] devices = engine.CaptureDevices;
            Console.WriteLine($"capture_endpoint_count={devices.Length.ToString(CultureInfo.InvariantCulture)}");
            foreach (DeviceInfo device in devices)
            {
                string id = GetStableEndpointId(device);
                string displayId = string.IsNullOrEmpty(id) ? "<unavailable>" : Quote(id);
                Console.WriteLine(
                    $"capture_endpoint id={displayId} name={Quote(device.Name)} "
                    + $"default={device.IsDefault.ToString().ToLowerInvariant()} "
                    + $"formats={DescribeFormats(device.SupportedDataFormats)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"capture enumeration failed ({ex.GetType().Name})");
            return 1;
        }
    }

    /// <summary>
    /// Captures one exact endpoint for a bounded duration and prints aggregate-only
    /// signal statistics. The endpoint ID is matched against the current enumeration.
    /// </summary>
    public static int Run(string endpointId, int durationMilliseconds)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("refused: Windows capture diagnostics require Windows WASAPI");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(endpointId)
            || endpointId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("refused: --endpoint-id must be a non-default exact endpoint ID");
            return 2;
        }

        if (durationMilliseconds is < 100 or > MaximumDurationMilliseconds)
        {
            Console.Error.WriteLine(
                $"refused: capture duration must be between 100 and {MaximumDurationMilliseconds} ms");
            return 2;
        }

        try
        {
            using MiniAudioEngine engine = new MiniAudioEngine();
            engine.UpdateAudioDevicesInfo();
            List<DeviceInfo> matches = engine.CaptureDevices
                .Where(device => IsExactEndpointMatch(endpointId, GetStableEndpointId(device)))
                .ToList();

            if (matches.Count != 1)
            {
                Console.Error.WriteLine(
                    $"refused: exact endpoint ID matched {matches.Count.ToString(CultureInfo.InvariantCulture)} "
                    + "current capture endpoints; no name/default fallback is allowed");
                return 2;
            }

            DeviceInfo endpoint = matches[0];
            AudioFormat requested = new AudioFormat
            {
                Format = SampleFormat.F32,
                Channels = 2,
                Layout = ChannelLayout.Stereo,
                SampleRate = 48000
            };
            MiniAudioDeviceConfig config = new MiniAudioDeviceConfig
            {
                PeriodSizeInFrames = 480,
                Capture = new DeviceSubConfig
                {
                    ShareMode = ShareMode.Shared
                },
                Wasapi = new WasapiSettings
                {
                    NoAutoConvertSRC = false
                }
            };

            using AudioCaptureDevice capture = engine.InitializeCaptureDevice(
                endpoint, requested, config);
            AudioFormat actual = capture.Format;
            if (actual.SampleRate <= 0 || actual.Channels is < 1 or > 8)
            {
                Console.Error.WriteLine(
                    $"refused: capture negotiated unsupported format rate={actual.SampleRate} "
                    + $"channels={actual.Channels}");
                return 2;
            }

            BluetoothMicrophoneSignalAccumulator signal =
                new BluetoothMicrophoneSignalAccumulator(actual.SampleRate, actual.Channels);
            AudioProcessCallback callback = (samples, _) => signal.ObserveFloat(samples);
            capture.OnAudioProcessed += callback;
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool cancelled = false;
            try
            {
                capture.Start();
                while (stopwatch.ElapsedMilliseconds < durationMilliseconds)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Thread.Sleep(10);
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                capture.OnAudioProcessed -= callback;
                if (capture.IsRunning)
                {
                    capture.Stop();
                }

                Console.CancelKeyPress -= cancelHandler;
            }

            BluetoothMicrophoneSignalSnapshot snapshot = signal.Snapshot();
            Console.WriteLine(
                $"capture endpoint_id={Quote(endpointId)} name={Quote(endpoint.Name)} "
                + $"requested_format={DescribeFormat(requested)} "
                + $"actual_format={DescribeFormat(actual)} "
                + $"cancelled={cancelled.ToString().ToLowerInvariant()} "
                + $"wall_duration_ms={stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}");
            Console.WriteLine(snapshot);
            if (cancelled)
            {
                return 4;
            }

            return snapshot.Frames > 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"capture failed ({ex.GetType().Name})");
            return 1;
        }
    }

    private static string GetStableEndpointId(DeviceInfo device)
        => DecodeWasapiEndpointId(device.Id);

    internal static string DecodeWasapiEndpointId(IntPtr nativeId)
    {
        if (nativeId == IntPtr.Zero)
        {
            return string.Empty;
        }

        // ma_device_id.wasapi is a fixed array of 64 Win32 WCHARs, not UTF-8
        // and not a pointer to a string. Read only that bounded native field.
        // This diagnostic accepts capture MMDevice IDs only: any other backend,
        // truncated ID or missing terminator fails closed, without a fallback.
        const string capturePrefix = "{0.0.1.00000000}.";
        Span<char> characters = stackalloc char[64];
        for (int index = 0; index < characters.Length; index++)
        {
            char character = (char)(ushort)Marshal.ReadInt16(nativeId, index * sizeof(char));
            if (character == '\0')
            {
                ReadOnlySpan<char> id = characters[..index];
                return id.Length == capturePrefix.Length + 38
                    && id.StartsWith(capturePrefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(id[capturePrefix.Length..], "B", out _)
                    ? new string(id)
                    : string.Empty;
            }

            characters[index] = character;
        }

        return string.Empty;
    }

    /// <summary>
    /// Exact identity policy used by the capture command. Friendly names, default
    /// aliases, and case-folded matches are intentionally not accepted.
    /// </summary>
    internal static bool IsExactEndpointMatch(string requestedId, string enumeratedId)
    {
        return !string.IsNullOrWhiteSpace(requestedId)
            && !requestedId.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(enumeratedId)
            && string.Equals(requestedId, enumeratedId, StringComparison.Ordinal);
    }

    private static string DescribeFormats(NativeDataFormat[]? formats)
    {
        if (formats is null || formats.Length == 0)
        {
            return "unavailable";
        }

        return string.Join(
            ",",
            formats.Select(format =>
                $"{format.Format}/{format.Channels}ch/{format.SampleRate}Hz"));
    }

    private static string DescribeFormat(AudioFormat format) =>
        $"{format.Format}/{format.Channels}ch/{format.SampleRate}Hz";

    private static string Quote(string value) =>
        '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}
