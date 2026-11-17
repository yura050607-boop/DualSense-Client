using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DualSenseClient.VIIPER;
using DualSenseClient.VIIPER.Callbacks;
using DualSenseClient.VIIPER.DualSense;

namespace DualSenseClient.Tests.Controllers.Emulation;

/// <summary>
/// Exercises the bundled native USB/IP path without attaching a system driver.
/// This keeps callback regressions distinct from the pure payload translation tests.
/// </summary>
[NonParallelizable]
public class NativeVirtualDualSenseOutputTests
{
    [Test]
    [Platform(Include = "Win")]
    public void UsbIpOutputTransferInvokesFullStateCallback()
    {
        int port = GetFreePort();
        USBServerConfig config = new USBServerConfig
        {
            addr = $"127.0.0.1:{port}",
            connection_timeout_ms = 2000,
            device_handler_connect_timeout_ms = 2000,
            write_batch_flush_interval_ms = 0
        };

        nuint server = 0;
        nuint device = 0;
        uint bus = 0;
        try
        {
            Assert.That(LibVIIPER.NewUSBServer(ref config, out server, null), Is.True);
            Assert.That(LibVIIPER.CreateUSBBus(server, ref bus), Is.True);
            Assert.That(LibVIIPER.CreateDualSenseDevice(server, out device, bus,
                autoAttachLocalhost: false, idVendor: 0, idProduct: 0, meta: null), Is.True);

            using TcpClient control = new TcpClient();
            control.Connect(IPAddress.Loopback, port);
            using NetworkStream controlStream = control.GetStream();
            WriteManagementHeader(controlStream, 0x8005);
            byte[] devListHeader = ReadExactly(controlStream, 12);
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(devListHeader.AsSpan(0, 2)), Is.EqualTo(0x0111));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(devListHeader.AsSpan(2, 2)), Is.EqualTo(0x0005));
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(devListHeader.AsSpan(8, 4)), Is.EqualTo(1));
            byte[] exported = ReadExactly(controlStream, 312);
            int interfaceCount = exported[311];
            _ = ReadExactly(controlStream, interfaceCount * 4);
            string busId = ReadCString(exported.AsSpan(256, 32));

            using TcpClient urb = new TcpClient();
            urb.Connect(IPAddress.Loopback, port);
            using NetworkStream urbStream = urb.GetStream();
            WriteManagementHeader(urbStream, 0x8003);
            byte[] busIdBytes = new byte[32];
            System.Text.Encoding.ASCII.GetBytes(busId).AsSpan().CopyTo(busIdBytes);
            urbStream.Write(busIdBytes);
            byte[] importHeader = ReadExactly(urbStream, 8);
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(importHeader.AsSpan(0, 2)), Is.EqualTo(0x0111));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(importHeader.AsSpan(2, 2)), Is.EqualTo(0x0003));
            _ = ReadExactly(urbStream, 312);

            TaskCompletionSource<DSOutputState> callback =
                new TaskCompletionSource<DSOutputState>(TaskCreationOptions.RunContinuationsAsynchronously);
            DSOutputStateCallback callbackDelegate = (handle, output) => callback.TrySetResult(output);
            Assert.That(LibVIIPER.SetDualSenseOutputStateCallback(device, callbackDelegate), Is.True);

            byte[] report = new byte[48];
            report[0] = 0x02;
            report[1] = 0x0C;
            report[11] = 0x01;
            report[12] = 0x20;
            report[13] = 0xE0;
            report[20] = 0x07;
            report[22] = 0x01;
            report[23] = 0x30;
            report[24] = 0xD0;
            report[31] = 0x08;
            WriteSubmit(urbStream, seq: 0, endpoint: 3, report);
            byte[] response = ReadExactly(urbStream, 48);

            Assert.Multiple(() =>
            {
                Assert.That(BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(0, 4)), Is.EqualTo(3));
                Assert.That(BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(20, 4)), Is.Zero);
                Assert.That(BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(24, 4)), Is.EqualTo(48));
            });

            Assert.That(callback.Task.Wait(TimeSpan.FromSeconds(2)), Is.True,
                "the native USB/IP output path must invoke the registered managed callback");
            DSOutputState state = callback.Task.GetAwaiter().GetResult();
            Assert.Multiple(() =>
            {
                Assert.That(state.RawOutputReport, Has.Length.EqualTo(48));
                Assert.That(state.RawOutputReport[0], Is.EqualTo(0x02));
                Assert.That(state.RawOutputReport[11], Is.EqualTo(0x01));
                Assert.That(state.RawOutputReport[20], Is.EqualTo(0x07));
                Assert.That(state.RawOutputReport[22], Is.EqualTo(0x01));
                Assert.That(state.RawOutputReport[31], Is.EqualTo(0x08));
            });
            GC.KeepAlive(callbackDelegate);
        }
        finally
        {
            if (device != 0)
            {
                _ = LibVIIPER.RemoveDualSenseDevice(device);
            }

            if (server != 0 && bus != 0)
            {
                _ = LibVIIPER.RemoveUSBBus(server, bus);
            }

            if (server != 0)
            {
                _ = LibVIIPER.CloseUSBServer(server);
            }
        }
    }

    private static int GetFreePort()
    {
        using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void WriteManagementHeader(NetworkStream stream, ushort command)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), 0x0111);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), command);
        stream.Write(header);
    }

    private static void WriteSubmit(NetworkStream stream, uint seq, uint endpoint, byte[] payload)
    {
        byte[] header = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), endpoint);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), (uint)payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32, 4), -1);
        stream.Write(header);
        stream.Write(payload);
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        byte[] buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string ReadCString(ReadOnlySpan<byte> value)
    {
        int length = value.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(length < 0 ? value : value[..length]);
    }
}
