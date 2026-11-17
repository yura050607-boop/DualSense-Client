using DualSenseClient.Controllers.DualSense.Input;
using DualSenseClient.Controllers.Emulation;

namespace DualSenseClient.Tests.Controllers.Emulation;

public sealed class VirtualDualSenseControllerInputTests
{
    [TestCase(0x00, 0x18)]
    [TestCase(0x01, 0x19)]
    [TestCase(0x02, 0x1A)]
    [TestCase(0x04, 0x1C)]
    [TestCase(0x1F, 0x1F)]
    [TestCase(0xE0, 0x18)]
    public void ConnectionStatusForVirtualUsb_PreservesAudioFlagsAndForcesUsbTransport(
        byte physicalRaw, byte expectedVirtualRaw)
    {
        byte actual = VirtualDualSenseController.NormalizeConnectionStatusForVirtualUsb(
            new ConnectionStatus(physicalRaw));

        Assert.That(actual, Is.EqualTo(expectedVirtualRaw));
    }
}
