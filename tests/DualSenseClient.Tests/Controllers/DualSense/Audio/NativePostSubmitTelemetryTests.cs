using System.Text;
using DualSenseClient.Controllers.DualSense.Audio;
using DualSenseClient.Controllers.Emulation;
using DualSenseClient.Controllers.DualSense.Output;
using DualSenseClient.VIIPER.DualSense;

namespace DualSenseClient.Tests.Controllers.DualSense.Audio;

public sealed class NativePostSubmitTelemetryTests
{
    private const string ValidJson = "{\"inputStatesReceived\":41,\"inputStatesSelected\":39,\"inputTransitionOverflows\":2,\"inputLatencyDistributions\":{\"ReceiveToPresented\":{\"Count\":35,\"MeanNs\":1}},\"serialNumber\":\"PRIVATE\"}";

    [Test]
    public void Reader_ExtractsOnlyAllowlistedUnsignedAggregateCounters()
    {
        NativeDeviceInputTelemetry result = ReadJson(ValidJson);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PostSubmitTelemetryStatus.Ok));
            Assert.That(result.Received, Is.EqualTo(41));
            Assert.That(result.Selected, Is.EqualTo(39));
            Assert.That(result.TransitionOverflows, Is.EqualTo(2));
            Assert.That(result.PresentedSamples, Is.EqualTo(35));
        });
    }

    [Test]
    public void Reader_AcceptsAnExactCapacityBufferWhenNativeReturnsThatSize()
    {
        int capacity = NativePostSubmitTelemetryReader.BufferCapacity;
        const string prefix = "{\"inputStatesReceived\":41,\"inputStatesSelected\":39,\"inputTransitionOverflows\":2,\"inputLatencyDistributions\":{\"ReceiveToPresented\":{\"Count\":35}},\"padding\":\"";
        const string suffix = "\"}";
        int spaces = capacity - 1 - Encoding.UTF8.GetByteCount(prefix + suffix);
        string json = prefix + new string(' ', spaces) + suffix;

        NativeDeviceInputTelemetry result = ReadJson(json);

        Assert.That(result.Status, Is.EqualTo(PostSubmitTelemetryStatus.Ok));
    }

    [TestCase("unavailable", "return-zero")]
    [TestCase("oversized", "oversized")]
    [TestCase("malformed", "malformed-json")]
    [TestCase("malformed", "invalid-utf8")]
    [TestCase("malformed", "missing-terminator")]
    [TestCase("malformed", "embedded-terminator")]
    [TestCase("malformed", "positive-without-write")]
    [TestCase("invalid_schema", "missing-counter")]
    [TestCase("invalid_schema", "negative-counter")]
    [TestCase("invalid_schema", "duplicate-counter")]
    [TestCase("native_error", "native-exception")]
    public void Reader_FailsClosedWithAStatusInsteadOfReportingZeroProgress(
        string expectedStatus,
        string failure)
    {
        byte[] buffer = new byte[NativePostSubmitTelemetryReader.BufferCapacity];
        NativeDeviceInputTelemetry result = failure switch
        {
            "return-zero" => NativePostSubmitTelemetryReader.Read((_, _, _) => 0, 17, buffer),
            "oversized" => NativePostSubmitTelemetryReader.Read(
                (_, _, _) => (nuint)buffer.Length + 1,
                17,
                buffer),
            "malformed-json" => ReadRaw([ (byte)'{', 0 ], 2),
            "invalid-utf8" => ReadRaw([ 0xFF, 0 ], 2),
            "missing-terminator" => ReadRaw(Encoding.UTF8.GetBytes(ValidJson), Encoding.UTF8.GetByteCount(ValidJson)),
            "embedded-terminator" => ReadRaw(Encoding.UTF8.GetBytes("{}\0{}\0"), 6),
            "positive-without-write" => ReadPositiveWithoutWrite(),
            "missing-counter" => ReadJson("{\"inputStatesReceived\":1}"),
            "negative-counter" => ReadJson(ValidJson.Replace("\"inputStatesReceived\":41", "\"inputStatesReceived\":-1", StringComparison.Ordinal)),
            "duplicate-counter" => ReadJson(ValidJson.Replace("\"inputStatesReceived\":41", "\"inputStatesReceived\":41,\"inputStatesReceived\":41", StringComparison.Ordinal)),
            "native-exception" => NativePostSubmitTelemetryReader.Read((_, _, _) => throw new InvalidOperationException("not logged"), 17, buffer),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        Assert.That(new PostSubmitTelemetrySnapshot(result.Status).StatusToken, Is.EqualTo(expectedStatus), failure);
        Assert.That(result.Received, Is.Null, "an unavailable or invalid sample must not resemble a zero counter");
    }

    [Test]
    public void VirtualController_UsesTheNativeReaderAndStopsSamplingAfterDispose()
    {
        int calls = 0;
        byte[] payload = Encoding.UTF8.GetBytes(ValidJson + "\0");
        using VirtualDualSenseController controller = new(
            new NoOpOutputs(),
            NativeAudioTimingDiagnostics.CreateDisabledForTesting(),
            (_, _) => true,
            testHandle: 17,
            getDeviceTelemetry: (_, buffer, size) =>
            {
                calls++;
                Assert.That(size, Is.EqualTo((nuint)buffer!.Length));
                Buffer.BlockCopy(payload, 0, buffer, 0, payload.Length);
                return (nuint)payload.Length;
            });

        NativeDeviceInputTelemetry first = controller.CapturePostSubmitInputTelemetry();
        controller.Dispose();
        NativeDeviceInputTelemetry afterDispose = controller.CapturePostSubmitInputTelemetry();

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(PostSubmitTelemetryStatus.Ok));
            Assert.That(first.PresentedSamples, Is.EqualTo(35));
            Assert.That(afterDispose.Status, Is.EqualTo(PostSubmitTelemetryStatus.Unavailable));
            Assert.That(calls, Is.EqualTo(1), "the disposed borrowed handle must never be queried again");
        });
    }

    [Test]
    public void EmulationAggregation_SumsTargetsWithOpaqueGenerationAndCount()
    {
        using VirtualDualSenseController first = CreateController(
            "{\"inputStatesReceived\":10,\"inputStatesSelected\":9,\"inputTransitionOverflows\":1,\"inputLatencyDistributions\":{\"ReceiveToPresented\":{\"Count\":8}}}");
        using VirtualDualSenseController second = CreateController(
            "{\"inputStatesReceived\":20,\"inputStatesSelected\":18,\"inputTransitionOverflows\":2,\"inputLatencyDistributions\":{\"ReceiveToPresented\":{\"Count\":15}}}");

        PostSubmitTelemetrySnapshot result = EmulationService.AggregatePostSubmitTelemetry([first, second], generation: 42);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PostSubmitTelemetryStatus.Ok));
            Assert.That(result.Generation, Is.EqualTo(42));
            Assert.That(result.TargetCount, Is.EqualTo(2));
            Assert.That(result.Received, Is.EqualTo(30));
            Assert.That(result.Selected, Is.EqualTo(27));
            Assert.That(result.TransitionOverflows, Is.EqualTo(3));
            Assert.That(result.PresentedSamples, Is.EqualTo(23));
        });
    }

    [Test]
    public void EmulationAggregation_AnyUnavailableTargetInvalidatesTheAggregateInsteadOfSummingPartialProgress()
    {
        using VirtualDualSenseController first = CreateController(
            "{\"inputStatesReceived\":10,\"inputStatesSelected\":9,\"inputTransitionOverflows\":1,\"inputLatencyDistributions\":{\"ReceiveToPresented\":{\"Count\":8}}}");
        using VirtualDualSenseController second = CreateControllerWithReader((_, _, _) => 0);

        PostSubmitTelemetrySnapshot result = EmulationService.AggregatePostSubmitTelemetry([first, second], generation: 43);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(PostSubmitTelemetryStatus.Unavailable));
            Assert.That(result.Generation, Is.EqualTo(43));
            Assert.That(result.TargetCount, Is.EqualTo(2));
            Assert.That(result.Received, Is.Null);
            Assert.That(result.PresentedSamples, Is.Null);
        });
    }

    private static NativeDeviceInputTelemetry ReadJson(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\0");
        return NativePostSubmitTelemetryReader.Read(
            (_, buffer, size) =>
            {
                if (bytes.Length <= (int)size)
                {
                    Buffer.BlockCopy(bytes, 0, buffer!, 0, bytes.Length);
                }

                return (nuint)bytes.Length;
            },
            17,
            new byte[NativePostSubmitTelemetryReader.BufferCapacity]);
    }

    private static NativeDeviceInputTelemetry ReadRaw(byte[] bytes, int required) =>
        NativePostSubmitTelemetryReader.Read(
            (_, buffer, size) =>
            {
                if (required <= (int)size)
                {
                    Buffer.BlockCopy(bytes, 0, buffer!, 0, required);
                }

                return (nuint)required;
            },
            17,
            new byte[NativePostSubmitTelemetryReader.BufferCapacity]);

    private static NativeDeviceInputTelemetry ReadPositiveWithoutWrite()
    {
        byte[] buffer = new byte[NativePostSubmitTelemetryReader.BufferCapacity];
        int required = Encoding.UTF8.GetByteCount(ValidJson) + 1;
        Buffer.BlockCopy(Encoding.UTF8.GetBytes(ValidJson + "\0"), 0, buffer, 0, required);
        return NativePostSubmitTelemetryReader.Read((_, _, _) => (nuint)required, 17, buffer);
    }

    private static VirtualDualSenseController CreateController(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json + "\0");
        return CreateControllerWithReader((_, buffer, size) =>
        {
            if (payload.Length <= (int)size)
            {
                Buffer.BlockCopy(payload, 0, buffer!, 0, payload.Length);
            }

            return (nuint)payload.Length;
        });
    }

    private static VirtualDualSenseController CreateControllerWithReader(Func<nuint, byte[]?, nuint, nuint> reader) =>
        new(
            new NoOpOutputs(),
            NativeAudioTimingDiagnostics.CreateDisabledForTesting(),
            (_, _) => true,
            testHandle: 17,
            getDeviceTelemetry: reader);

    private sealed class NoOpOutputs : IDualSenseOutputs
    {
        public void SetVibration(byte left, byte right) { }
        public void SendOutputState(SetStateData payload) { }
    }
}
