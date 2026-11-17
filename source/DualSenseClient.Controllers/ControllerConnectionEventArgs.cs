using DualSenseClient.Hid;

namespace DualSenseClient.Controllers;

/// <summary>
/// Event data for controller connection and disconnection events.
/// Carries the raw device info, the resolved controller type, and the live controller
/// instance associated with the stable path when one is still owned by the scanner.
/// </summary>
public sealed class ControllerConnectionEventArgs : EventArgs
{
    /// <summary>
    /// Whether the controller connected or disconnected.
    /// </summary>
    public DeviceChangeType ChangeType { get; }

    /// <summary>
    /// Information about the underlying HID device that connected or disconnected.
    /// Always available regardless of connection state.
    /// </summary>
    public IHidDeviceInfo Info { get; }

    /// <summary>
    /// The resolved controller type.
    /// </summary>
    public ControllerType Type { get; }

    /// <summary>
    /// The physical transport (USB or Bluetooth).
    /// </summary>
    public ConnectionType ConnectionType
    {
        get
        {
            return Info.BusType;
        }
    }

    /// <summary>
    /// The live controller instance associated with this path, or <c>null</c> when the
    /// scanner did not own an opened instance (for example an untracked initial event).
    /// </summary>
    public IControllerDevice? Controller { get; }

    /// <summary>
    /// Creates a new <see cref="ControllerConnectionEventArgs"/> instance.
    /// </summary>
    /// <param name="changeType">Whether the controller connected or disconnected.</param>
    /// <param name="info">Information about the underlying HID device.</param>
    /// <param name="type">The resolved controller type.</param>
    /// <param name="controller">
    /// The live controller (non-null on connect, and normally also on a disconnect for
    /// a path opened by this scanner).
    /// </param>
    public ControllerConnectionEventArgs(
        DeviceChangeType changeType,
        IHidDeviceInfo info,
        ControllerType type,
        IControllerDevice? controller)
    {
        ChangeType = changeType;
        Info = info;
        Type = type;
        Controller = controller;
    }
}
