namespace DualSenseClient.Controllers.Emulation;

/// <summary>
/// Separates the identity that passed discovery from paths merely observed while
/// deciding whether an attachment was safe. Observed paths remain available for
/// cleanup, but they never authorize a virtual controller on their own.
/// </summary>
internal sealed class VirtualDeviceDiscoveryResult
{
    internal VirtualDeviceDiscoveryResult(string? acceptedPath, IEnumerable<string> observedPaths)
    {
        AcceptedPath = acceptedPath;
        ObservedPaths = observedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one path that passed the stable, unique discovery gate, or <see langword="null"/>
    /// when discovery timed out or remained ambiguous.
    /// </summary>
    internal string? AcceptedPath { get; }

    /// <summary>
    /// Every candidate observed during discovery, including candidates that vanished.
    /// </summary>
    internal IReadOnlyCollection<string> ObservedPaths { get; }
}

/// <summary>
/// Tracks HID paths observed while a single virtual USB/IP device is attaching.
///
/// HID enumeration does not identify which USB/IP import produced a matching path.
/// Consequently a path is accepted only after the same, unique candidate has been
/// observed for several polls and no other candidate (including one already owned by
/// this service) has appeared.  A second candidate is deliberately sticky: accepting
/// one after the other disappeared would make enumeration order decide device
/// ownership, which is the duplicate-attachment failure this state protects against.
/// </summary>
internal sealed class VirtualDeviceCandidateTracker
{
    internal const int RequiredStablePolls = 5;

    private readonly HashSet<string> _baselinePaths;
    private readonly HashSet<string> _physicalPaths;
    private readonly HashSet<string> _ownedPaths;
    private readonly HashSet<string> _seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _lastPresentCandidates;
    private string? _acceptedPath;
    private int _stablePolls;

    internal VirtualDeviceCandidateTracker(IEnumerable<string> baselinePaths,
        string? physicalPath, IEnumerable<string> ownedPaths)
    {
        _baselinePaths = baselinePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _physicalPaths = physicalPath is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(new[] { physicalPath }, StringComparer.OrdinalIgnoreCase);
        _ownedPaths = ownedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All paths that have appeared as candidates, including paths that later vanish.
    /// </summary>
    internal IReadOnlyCollection<string> SeenPaths => _seenPaths;

    /// <summary>
    /// Returns the caller-facing discovery result. A path that was only observed is
    /// intentionally not promoted to an accepted identity when discovery times out.
    /// </summary>
    internal VirtualDeviceDiscoveryResult GetResult()
        => new VirtualDeviceDiscoveryResult(_acceptedPath, _seenPaths);

    /// <summary>
    /// Records one HID enumeration snapshot. Returns <see langword="true"/> only
    /// when one candidate has remained the sole observed candidate for the required
    /// number of consecutive polls.
    /// </summary>
    internal bool Observe(IEnumerable<string> paths)
    {
        HashSet<string> presentCandidates = paths
            .Where(path => !_baselinePaths.Contains(path)
                           && !_physicalPaths.Contains(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string path in presentCandidates)
        {
            _seenPaths.Add(path);
        }

        bool ownedCandidateObserved = _seenPaths.Any(_ownedPaths.Contains);
        bool uniqueStableCandidate = !ownedCandidateObserved
                                     && _seenPaths.Count == 1
                                     && presentCandidates.Count == 1;
        if (uniqueStableCandidate)
        {
            _stablePolls = _lastPresentCandidates is not null
                           && presentCandidates.SetEquals(_lastPresentCandidates)
                ? _stablePolls + 1
                : 1;
        }
        else
        {
            _stablePolls = 0;
        }

        _lastPresentCandidates = presentCandidates;
        if (uniqueStableCandidate && _stablePolls >= RequiredStablePolls)
        {
            _acceptedPath = presentCandidates.Single();
            return true;
        }

        return false;
    }
}
