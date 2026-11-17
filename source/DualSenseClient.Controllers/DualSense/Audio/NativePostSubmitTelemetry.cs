using System.Text.Json;

namespace DualSenseClient.Controllers.DualSense.Audio;

internal enum PostSubmitTelemetryStatus
{
    NotSampled,
    NoSource,
    NoTarget,
    Busy,
    Stopped,
    Stale,
    Ok,
    Unavailable,
    Oversized,
    Malformed,
    InvalidSchema,
    NativeError
}

internal sealed record PostSubmitTelemetrySnapshot(
    PostSubmitTelemetryStatus Status,
    long? Generation = null,
    int? TargetCount = null,
    ulong? Received = null,
    ulong? Selected = null,
    ulong? TransitionOverflows = null,
    ulong? PresentedSamples = null)
{
    internal string StatusToken => Status switch
    {
        PostSubmitTelemetryStatus.NotSampled => "not_sampled",
        PostSubmitTelemetryStatus.NoSource => "no_source",
        PostSubmitTelemetryStatus.NoTarget => "no_target",
        PostSubmitTelemetryStatus.Busy => "busy",
        PostSubmitTelemetryStatus.Stopped => "stopped",
        PostSubmitTelemetryStatus.Stale => "stale",
        PostSubmitTelemetryStatus.Ok => "ok",
        PostSubmitTelemetryStatus.Unavailable => "unavailable",
        PostSubmitTelemetryStatus.Oversized => "oversized",
        PostSubmitTelemetryStatus.Malformed => "malformed",
        PostSubmitTelemetryStatus.InvalidSchema => "invalid_schema",
        PostSubmitTelemetryStatus.NativeError => "native_error",
        _ => "unavailable"
    };

    internal static PostSubmitTelemetrySnapshot NotSampled { get; } =
        new(PostSubmitTelemetryStatus.NotSampled);
}

internal readonly record struct NativeDeviceInputTelemetry(
    PostSubmitTelemetryStatus Status,
    ulong? Received = null,
    ulong? Selected = null,
    ulong? TransitionOverflows = null,
    ulong? PresentedSamples = null);

/// <summary>
/// Reads only the fixed, aggregate input counters from VIIPER's existing JSON export.
/// The caller supplies a bounded reusable buffer and must already own the device
/// lifetime; this helper never creates, retains, or releases a native handle.
/// </summary>
internal static class NativePostSubmitTelemetryReader
{
    internal const int BufferCapacity = 16 * 1024;

    internal static NativeDeviceInputTelemetry Read(
        Func<nuint, byte[]?, nuint, nuint> getDeviceTelemetry,
        nuint deviceHandle,
        byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(getDeviceTelemetry);
        ArgumentNullException.ThrowIfNull(buffer);
        if (deviceHandle == 0 || buffer.Length == 0)
        {
            return new(PostSubmitTelemetryStatus.Unavailable);
        }

        nuint required;
        try
        {
            Array.Clear(buffer);
            required = getDeviceTelemetry(deviceHandle, buffer, (nuint)buffer.Length);
        }
        catch
        {
            return new(PostSubmitTelemetryStatus.NativeError);
        }

        if (required == 0)
        {
            return new(PostSubmitTelemetryStatus.Unavailable);
        }

        if (required > (nuint)buffer.Length)
        {
            return new(PostSubmitTelemetryStatus.Oversized);
        }

        int length = checked((int)required);
        if (length < 2 || buffer[length - 1] != 0 || buffer.AsSpan(0, length - 1).Contains((byte)0))
        {
            return new(PostSubmitTelemetryStatus.Malformed);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                buffer.AsMemory(0, length - 1),
                new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetUniqueProperty(root, "inputStatesReceived", out JsonElement received)
                || !TryGetUniqueProperty(root, "inputStatesSelected", out JsonElement selected)
                || !TryGetUniqueProperty(root, "inputTransitionOverflows", out JsonElement overflows)
                || !TryGetUniqueProperty(root, "inputLatencyDistributions", out JsonElement distributions)
                || distributions.ValueKind != JsonValueKind.Object
                || !TryGetUniqueProperty(distributions, "ReceiveToPresented", out JsonElement receiveToPresented)
                || receiveToPresented.ValueKind != JsonValueKind.Object
                || !TryGetUniqueProperty(receiveToPresented, "Count", out JsonElement presented)
                || !TryGetUInt64(received, out ulong receivedValue)
                || !TryGetUInt64(selected, out ulong selectedValue)
                || !TryGetUInt64(overflows, out ulong overflowValue)
                || !TryGetUInt64(presented, out ulong presentedValue))
            {
                return new(PostSubmitTelemetryStatus.InvalidSchema);
            }

            return new(
                PostSubmitTelemetryStatus.Ok,
                receivedValue,
                selectedValue,
                overflowValue,
                presentedValue);
        }
        catch (JsonException)
        {
            return new(PostSubmitTelemetryStatus.Malformed);
        }
        catch (ArgumentException)
        {
            return new(PostSubmitTelemetryStatus.Malformed);
        }
    }

    private static bool TryGetUniqueProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        bool found = false;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!property.NameEquals(name))
            {
                continue;
            }

            if (found)
            {
                return false;
            }

            found = true;
            value = property.Value;
        }

        return found;
    }

    private static bool TryGetUInt64(JsonElement element, out ulong value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
