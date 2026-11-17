using DualSenseClient.Hid;
using DualSenseClient.Logging;

namespace DualSenseClient.Controllers.DualSense.Feature;

/// <summary>
/// Reads and parses DualSense feature reports.
/// Returns null instead of throwing when a report cannot be read, so callers
/// can degrade gracefully (e.g. an unsupported transport or disconnected device).
/// </summary>
public static class FeatureReader
{
    /// <summary>
    /// Logger instance.
    /// </summary>
    private static readonly DualSenseClientLogger _log = DualSenseClientLogger.For("FeatureReader");

    /// <summary>
    /// Reads the Bluetooth calibration feature report once after a controller opens.
    ///
    /// DualSense firmware can start a Bluetooth connection in the 10-byte simplified
    /// input-report mode. A GET for feature report 0x05 is the host initialization probe
    /// used by the maintained Bluetooth path before the full 0x31 report is consumed;
    /// the calibration values are not currently consumed by the application. If the
    /// controller still emits a compact 0x01 report, DualSenseDevice sends an output
    /// wake report as the transport-level fallback.
    /// </summary>
    /// <param name="device">The Bluetooth controller device to prime.</param>
    /// <returns><c>true</c> when a report with ID 0x05 was returned; otherwise <c>false</c>.</returns>
    public static bool PrimeBluetoothInputMode(IControllerDevice device)
    {
        try
        {
            byte[] raw = device.GetFeatureReport(0x05, 64);
            if (raw.Length == 0 || raw[0] != 0x05)
            {
                byte reportId = raw.Length > 0 ? raw[0] : (byte)0;
                _log.Warning($"Bluetooth calibration report was not valid (report ID 0x{reportId:X2})");
                return false;
            }

            _log.Debug("Bluetooth input mode primed with calibration feature report 0x05");
            return true;
        }
        catch (HidException ex)
        {
            // Some host Bluetooth stacks reject feature reads even though output
            // reports remain usable. Keep connecting and let the normal input/output
            // paths degrade gracefully when that happens.
            _log.Warning($"GetFeatureReport(0x05) failed while priming Bluetooth input mode: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads the pairing information feature report (0x09).
    /// </summary>
    /// <param name="device">The controller device to read from.</param>
    /// <returns>The parsed pairing info, or <c>null</c> if the read failed or the
    /// response was not a valid 0x09 report.</returns>
    public static PairingInfo? ReadPairingInfo(IControllerDevice device)
    {
        try
        {
            byte[] raw = device.GetFeatureReport(0x09, 20);
            PairingInfo info = new PairingInfo(raw);
            if (!info.IsValid)
            {
                byte reportId = raw.Length > 0 ? raw[0] : (byte)0;
                _log.Warning($"Pairing info report was not valid (report ID 0x{reportId:X2})");
                return null;
            }

            _log.Debug($"Pairing info: client {info.ClientMac}, host {info.HostMac}");
            return info;
        }
        catch (HidException ex)
        {
            _log.Error($"GetFeatureReport(0x09) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the firmware and hardware info feature report (0x20).
    /// </summary>
    /// <param name="device">The controller device to read from.</param>
    /// <returns>The parsed firmware info, or <c>null</c> if the read failed or the
    /// response was not a valid 0x20 report.</returns>
    public static FirmwareInfo? ReadFirmwareInfo(IControllerDevice device)
    {
        try
        {
            byte[] raw = device.GetFeatureReport(0x20, 64);
            FirmwareInfo info = new FirmwareInfo(raw);
            if (!info.IsValid)
            {
                byte reportId = raw.Length > 0 ? raw[0] : (byte)0;
                _log.Warning($"Firmware info report was not valid (report ID 0x{reportId:X2})");
                return null;
            }

            _log.Debug($"Firmware info: {info.MainFirmwareVersion} (model {info.ModelRevision}, built {info.BuildDate} {info.BuildTime})");
            return info;
        }
        catch (HidException ex)
        {
            _log.Error($"GetFeatureReport(0x20) failed: {ex.Message}");
            return null;
        }
    }
}
