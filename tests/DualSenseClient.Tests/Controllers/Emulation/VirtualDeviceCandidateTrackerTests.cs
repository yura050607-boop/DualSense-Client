using DualSenseClient.Controllers.Emulation;

namespace DualSenseClient.Tests.Controllers.Emulation;

public sealed class VirtualDeviceCandidateTrackerTests
{
    [Test]
    public void UniqueCandidate_MustRemainStableBeforeItIsSelected()
    {
        VirtualDeviceCandidateTracker tracker = new VirtualDeviceCandidateTracker(
            baselinePaths: ["hid:old"], physicalPath: "hid:physical", ownedPaths: []);

        Assert.That(tracker.Observe(["hid:old", "hid:new", "hid:physical"]), Is.False);
        for (int i = 1; i < VirtualDeviceCandidateTracker.RequiredStablePolls; i++)
        {
            Assert.That(tracker.Observe(["hid:physical", "hid:new"]), Is.EqualTo(
                i == VirtualDeviceCandidateTracker.RequiredStablePolls - 1));
        }

        Assert.That(tracker.SeenPaths, Is.EquivalentTo(["hid:new"]));
        Assert.That(tracker.GetResult().AcceptedPath, Is.EqualTo("hid:new"));
    }

    [Test]
    public void ReversedEnumerationOrder_DoesNotChangeTheSelectedIdentity()
    {
        VirtualDeviceCandidateTracker tracker = new VirtualDeviceCandidateTracker(
            baselinePaths: ["hid:physical"], physicalPath: "hid:physical", ownedPaths: []);

        for (int i = 0; i < VirtualDeviceCandidateTracker.RequiredStablePolls; i++)
        {
            IReadOnlyList<string> snapshot = i % 2 == 0
                ? ["hid:virtual", "hid:physical"]
                : ["hid:physical", "hid:virtual"];
            Assert.That(tracker.Observe(snapshot), Is.EqualTo(
                i == VirtualDeviceCandidateTracker.RequiredStablePolls - 1));
        }

        Assert.That(tracker.SeenPaths, Is.EquivalentTo(["hid:virtual"]));
        Assert.That(tracker.GetResult().AcceptedPath, Is.EqualTo("hid:virtual"));
    }

    [Test]
    public void PreviouslyOwnedCandidate_MakesASecondAttachAmbiguous()
    {
        VirtualDeviceCandidateTracker tracker = new VirtualDeviceCandidateTracker(
            baselinePaths: [], physicalPath: "hid:physical", ownedPaths: ["hid:old"]);

        for (int i = 0; i < VirtualDeviceCandidateTracker.RequiredStablePolls + 2; i++)
        {
            Assert.That(tracker.Observe(["hid:old", "hid:new", "hid:physical"]), Is.False);
        }

        Assert.That(tracker.SeenPaths, Is.EquivalentTo(["hid:old", "hid:new"]));
        Assert.That(tracker.GetResult().AcceptedPath, Is.Null,
            "an owned observation must not authorize the caller");
        Assert.That(tracker.GetResult().ObservedPaths, Is.EquivalentTo(["hid:old", "hid:new"]));
    }

    [Test]
    public void TwoMatchingDevices_AndPhysicalDeviceFailClosed()
    {
        VirtualDeviceCandidateTracker tracker = new VirtualDeviceCandidateTracker(
            baselinePaths: ["hid:physical"], physicalPath: "hid:physical", ownedPaths: []);

        for (int i = 0; i < VirtualDeviceCandidateTracker.RequiredStablePolls + 2; i++)
        {
            Assert.That(tracker.Observe(["hid:physical", "hid:virtual-a", "hid:virtual-b"]), Is.False);
        }

        Assert.That(tracker.SeenPaths, Is.EquivalentTo(["hid:virtual-a", "hid:virtual-b"]));
        Assert.That(tracker.GetResult().AcceptedPath, Is.Null,
            "ambiguous observations must remain cleanup-only");
    }

    [Test]
    public void TimeoutAfterUnstableSingleton_DoesNotPromoteObservedPath()
    {
        VirtualDeviceCandidateTracker tracker = new VirtualDeviceCandidateTracker(
            baselinePaths: [], physicalPath: "hid:physical", ownedPaths: []);

        for (int i = 0; i < VirtualDeviceCandidateTracker.RequiredStablePolls - 1; i++)
        {
            tracker.Observe(["hid:virtual"]);
        }

        VirtualDeviceDiscoveryResult result = tracker.GetResult();
        Assert.That(result.AcceptedPath, Is.Null,
            "a singleton that has not completed the stable-poll gate cannot be assigned");
        Assert.That(result.ObservedPaths, Is.EquivalentTo(["hid:virtual"]));
    }

    [Test]
    public void TimeoutAfterSingletonVanished_DoesNotPromoteObservedPath()
    {
        VirtualDeviceCandidateTracker tracker = new VirtualDeviceCandidateTracker(
            baselinePaths: [], physicalPath: "hid:physical", ownedPaths: []);

        tracker.Observe(["hid:virtual"]);
        for (int i = 0; i < VirtualDeviceCandidateTracker.RequiredStablePolls; i++)
        {
            tracker.Observe([]);
        }

        VirtualDeviceDiscoveryResult result = tracker.GetResult();
        Assert.That(result.AcceptedPath, Is.Null,
            "a vanished singleton seen before timeout cannot be assigned");
        Assert.That(result.ObservedPaths, Is.EquivalentTo(["hid:virtual"]));
    }

    [Test]
    public void TimeoutAfterOwnedSingleton_DoesNotPromoteObservedPath()
    {
        VirtualDeviceCandidateTracker tracker = new VirtualDeviceCandidateTracker(
            baselinePaths: [], physicalPath: "hid:physical", ownedPaths: ["hid:old"]);

        for (int i = 0; i < VirtualDeviceCandidateTracker.RequiredStablePolls + 1; i++)
        {
            tracker.Observe(["hid:old"]);
        }

        VirtualDeviceDiscoveryResult result = tracker.GetResult();
        Assert.That(result.AcceptedPath, Is.Null,
            "an already-owned singleton cannot be assigned to a new attachment");
        Assert.That(result.ObservedPaths, Is.EquivalentTo(["hid:old"]));
    }

    [Test]
    public void TimeoutAfterLateSingleton_DoesNotPromoteObservedPath()
    {
        VirtualDeviceCandidateTracker tracker = new VirtualDeviceCandidateTracker(
            baselinePaths: [], physicalPath: "hid:physical", ownedPaths: []);

        for (int i = 0; i < VirtualDeviceCandidateTracker.RequiredStablePolls - 1; i++)
        {
            tracker.Observe([]);
        }

        tracker.Observe(["hid:late"]);

        VirtualDeviceDiscoveryResult result = tracker.GetResult();
        Assert.That(result.AcceptedPath, Is.Null,
            "a late singleton that did not pass the stable-poll gate cannot be assigned");
        Assert.That(result.ObservedPaths, Is.EquivalentTo(["hid:late"]));
    }
}
