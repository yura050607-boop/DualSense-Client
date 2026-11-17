using System.Diagnostics;
using DualSenseClient.Controllers;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.DualSense.Feature;
using DualSenseClient.Hid;

namespace DualSenseBluetoothMicProbe;

internal static class Program
{
    private const ushort SonyVendorId = 0x054C;
    private const ushort StandardDualSenseProductId = 0x0CE6;
    private const int DefaultDurationMs = 3000;

    private static readonly string[] CompetingProcessNames =
    [
        "DualSenseClient",
        "DualSenseX",
        "DS4Windows",
        "DSX",
        "reWASD",
        "Steam",
        "SteamInput"
    ];

    private static int Main(string[] args)
    {
        if (!ProbeOptions.TryParse(args, out ProbeOptions options, out string? error))
        {
            Console.Error.WriteLine($"error: {error}");
            PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.SelfTest)
        {
            return ProtocolSelfTest.Run();
        }

        if (options.DryRun)
        {
            Console.WriteLine(
                $"dry-run target=standard-dualsense-bluetooth vid=0x{SonyVendorId:X4} pid=0x{StandardDualSenseProductId:X4} " +
                $"duration_ms={options.DurationMs} duplex=opt-in no-hardware=true");
            return 0;
        }

        if (options.ListCaptureEndpoints)
        {
            return WindowsCaptureSignalProbe.ListCaptureEndpoints();
        }

        if (options.Capture)
        {
            return WindowsCaptureSignalProbe.Run(options.EndpointId!, options.DurationMs);
        }

        if (options.DurationMs > 10000)
        {
            Console.Error.WriteLine("refused: physical Bluetooth probe duration must be between 100 and 10000 ms");
            return 2;
        }

        if (!TryFindCompetingProcesses(
                out IReadOnlyList<string> competing,
                out string? inspectionFailure))
        {
            Console.Error.WriteLine($"refused: process ownership inspection failed ({inspectionFailure})");
            return 1;
        }

        if (competing.Count > 0)
        {
            Console.Error.WriteLine($"refused: competing controller/audio process active ({string.Join(", ", competing)})");
            return 2;
        }

        return RunHardwareProbe(options);
    }

    private static int RunHardwareProbe(ProbeOptions options)
    {
        using HidDeviceEnumerator enumerator = new HidDeviceEnumerator();
        IReadOnlyList<IHidDeviceInfo> candidates;
        try
        {
            // Enumerate the complete standard VID/PID set first. Requiring exactly one
            // result rejects USB+BT ambiguity instead of silently choosing a transport.
            candidates = enumerator.Enumerate(SonyVendorId, StandardDualSenseProductId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"refused: HID enumeration failed ({ex.GetType().Name})");
            return 1;
        }

        if (candidates.Count != 1)
        {
            Console.Error.WriteLine($"refused: expected exactly one standard DualSense candidate, found {candidates.Count}; USB/virtual/ambiguous targets are not accepted");
            return 2;
        }

        IHidDeviceInfo info = candidates[0];
        if (info.BusType != ConnectionType.Bluetooth)
        {
            Console.Error.WriteLine($"refused: selected candidate is {info.BusType}; only standard DualSense Bluetooth is accepted");
            return 2;
        }

        IHidDevice? hid = null;
        try
        {
            IHidDevice selectedHid = enumerator.OpenDevice(info.Path);
            hid = selectedHid;
            byte[] pairingReport = selectedHid.GetFeatureReport(0x09, 20);
            PairingInfo pairing = new PairingInfo(pairingReport);
            if (!pairing.IsValid)
            {
                Console.Error.WriteLine("refused: pairing identity could not be validated");
                return 2;
            }

            if (VirtualDeviceFilter.IsKnownVirtualMac(pairing.ClientMac))
            {
                Console.Error.WriteLine("refused: selected target has a known virtual-controller identity");
                return 2;
            }

            using HidMicProbeTransport transport = new HidMicProbeTransport(selectedHid);
            hid = null;
            BluetoothMicrophoneSignalAccumulator signal =
                new BluetoothMicrophoneSignalAccumulator(48000, 2);
            using MicProbeSession session = new MicProbeSession(
                transport, TimeSpan.FromMilliseconds(options.DurationMs), signal);
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            MicProbeSummary summary;
            try
            {
                summary = session.Run(cancellation.Token);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            PrintSummary(summary, options.DurationMs);
            if (summary.Cancelled)
            {
                return 4;
            }

            if (summary.Counters.TeardownFailures != 0 || summary.Failure is not null)
            {
                return 1;
            }

            return summary.HasValidatedMicFrames ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"probe failed before teardown ({ex.GetType().Name})");
            return 1;
        }
        finally
        {
            hid?.Dispose();
        }
    }

    private static void PrintSummary(MicProbeSummary summary, int durationMs)
    {
        MicProbeCounters counters = summary.Counters;
        Console.WriteLine(
            $"target=standard-dualsense-bluetooth duration_ms={durationMs} activated={summary.Activated} " +
            $"cancelled={summary.Cancelled} disconnected={summary.Disconnected} " +
            $"reports={counters.ReportsRead} bytes={counters.BytesRead} timeouts={counters.ReadTimeouts} " +
            $"envelope_candidates={counters.EnvelopeCandidates} rejected={counters.RejectedCandidates} " +
            $"valid_opus_frames={counters.ValidOpusFrames} teardown_attempts={counters.TeardownAttempts} " +
            $"teardown_failures={counters.TeardownFailures}");
        if (summary.Failure is not null)
        {
            Console.WriteLine($"failure={summary.Failure}");
        }

        if (summary.Signal is not null)
        {
            Console.WriteLine(summary.Signal);
        }
    }

    private static bool TryFindCompetingProcesses(
        out IReadOnlyList<string> found,
        out string? inspectionFailure)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string processName in CompetingProcessNames)
        {
            try
            {
                if (Process.GetProcessesByName(processName).Length > 0)
                {
                    names.Add(processName);
                }
            }
            catch (Exception ex)
            {
                found = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
                inspectionFailure = $"{processName}:{ex.GetType().Name}";
                return false;
            }
        }

        found = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        inspectionFailure = null;
        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("DualSense Bluetooth built-in microphone feasibility probe");
        Console.WriteLine("  --help                         Show this help and do not touch HID");
        Console.WriteLine("  --dry-run                      Show the target/session plan and do not touch HID");
        Console.WriteLine("  --self-test                    Exercise parser and cancellation teardown with a fake transport");
        Console.WriteLine("  --run [--duration-ms N]        Opt in to one bounded physical Bluetooth probe (100..10000 ms)");
        Console.WriteLine("  --list-capture                 List exact current Windows capture endpoint IDs and formats");
        Console.WriteLine("  --capture --endpoint-id ID     Capture one exact endpoint (shared mode, aggregate-only, <=30 s)");
        Console.WriteLine("No arguments show help. The probe never publishes a virtual gamepad or stores raw reports/PCM.");
    }

    private sealed class ProbeOptions
    {
        public bool ShowHelp { get; private set; }
        public bool DryRun { get; private set; }
        public bool SelfTest { get; private set; }
        public bool ListCaptureEndpoints { get; private set; }
        public bool Capture { get; private set; }
        public string? EndpointId { get; private set; }
        public int DurationMs { get; private set; } = DefaultDurationMs;

        public static bool TryParse(string[] args, out ProbeOptions options, out string? error)
        {
            options = new ProbeOptions();
            error = null;
            if (args.Length == 0)
            {
                options.ShowHelp = true;
                return true;
            }

            bool run = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help":
                    case "-h":
                        options.ShowHelp = true;
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--self-test":
                        options.SelfTest = true;
                        break;
                    case "--list-capture":
                        options.ListCaptureEndpoints = true;
                        break;
                    case "--capture":
                        options.Capture = true;
                        break;
                    case "--endpoint-id" when i + 1 < args.Length:
                        options.EndpointId = args[++i];
                        break;
                    case "--run":
                        run = true;
                        break;
                    case "--duration-ms" when i + 1 < args.Length
                                               && int.TryParse(args[++i], out int duration):
                        if (duration is < 100 or > 30000)
                        {
                            error = "--duration-ms must be between 100 and 30000";
                            options = new ProbeOptions();
                            return false;
                        }

                        options.DurationMs = duration;
                        break;
                    default:
                        error = $"unknown or incomplete option '{args[i]}'";
                        options = new ProbeOptions();
                        return false;
                }
            }

            if (!options.ShowHelp && !options.DryRun && !options.SelfTest
                && !options.ListCaptureEndpoints && !options.Capture && !run)
            {
                error = "an explicit --run, --dry-run, --self-test, --list-capture, or --capture is required";
                options = new ProbeOptions();
                return false;
            }

            if (options.SelfTest && (options.DryRun || run || options.ListCaptureEndpoints || options.Capture))
            {
                error = "--self-test cannot be combined with another operation";
                options = new ProbeOptions();
                return false;
            }

            if (options.ListCaptureEndpoints && (options.DryRun || run || options.Capture
                    || options.EndpointId is not null))
            {
                error = "--list-capture cannot be combined with --run, --dry-run, --capture, or --endpoint-id";
                options = new ProbeOptions();
                return false;
            }

            if (options.Capture && (options.DryRun || run || options.EndpointId is null))
            {
                error = "--capture requires exactly one --endpoint-id and cannot be combined with --run or --dry-run";
                options = new ProbeOptions();
                return false;
            }

            if (!options.Capture && options.EndpointId is not null)
            {
                error = "--endpoint-id is only valid with --capture";
                options = new ProbeOptions();
                return false;
            }

            return true;
        }
    }
}
