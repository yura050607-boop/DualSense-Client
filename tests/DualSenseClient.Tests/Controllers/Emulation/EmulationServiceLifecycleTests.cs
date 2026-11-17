using DualSenseClient.Controllers.Emulation;

namespace DualSenseClient.Tests.Controllers.Emulation;

public sealed class EmulationServiceLifecycleTests
{
    [Test]
    public void OffToOnRefresh_WaitsForTheOwnedPathRemovedByTheOffRefresh()
    {
        Assert.That(
            EmulationService.ShouldSettleForPendingDetach(
                hadVirtual: false,
                creationInFlight: false,
                hasPendingDetachPath: true),
            Is.True,
            "the prior Off refresh already removed the Virtual instance, but its HID path can still be detaching");
    }

    [Test]
    public void NewAttachWithoutPriorDetach_DoesNotAddAnUnneededSettleWait()
    {
        Assert.That(
            EmulationService.ShouldSettleForPendingDetach(
                hadVirtual: false,
                creationInFlight: false,
                hasPendingDetachPath: false),
            Is.False);
    }

    [Test]
    public void ConfirmedDetach_RemovesOnlyAbsentPendingOwnedPaths()
    {
        HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase)
        {
            "HID\\pending-absent",
            "HID\\pending-present"
        };
        HashSet<string> owned = new(StringComparer.OrdinalIgnoreCase)
        {
            "HID\\pending-absent",
            "HID\\pending-present",
            "HID\\active-owned",
            "HID\\unrelated-owned"
        };
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase)
        {
            "hid\\pending-present",
            "hid\\active-owned",
            "hid\\unrelated-owned"
        };

        EmulationService.ForgetConfirmedDetachedPaths(pending, owned, present);

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.EquivalentTo(new[] { "HID\\pending-present" }));
            Assert.That(owned, Is.EquivalentTo(new[]
            {
                "HID\\pending-present",
                "HID\\active-owned",
                "HID\\unrelated-owned"
            }));
        });
    }

    [Test]
    public void NewerRefreshOrDisposal_MakesPendingWaitStale()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EmulationService.IsStaleGeneration(8, 7, disposed: false), Is.True,
                "an older creation wait must not reattach after a newer refresh");
            Assert.That(EmulationService.IsStaleGeneration(7, 7, disposed: true), Is.True,
                "a disposed service must cancel every outstanding creation wait");
            Assert.That(EmulationService.IsStaleGeneration(7, 7, disposed: false), Is.False);
        });
    }
}
