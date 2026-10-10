using NAudio.CoreAudioApi;
using System.Text;

namespace Edith.Audio;

public static class AudioDeviceService
{
    public static IReadOnlyList<AudioInputDeviceInfo>
        GetActiveCaptureDevices()
    {
        using var enumerator =
            new MMDeviceEnumerator();

        string? consoleId = null;
        string? communicationsId = null;

        try
        {
            using var device =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Console);

            consoleId = device.ID;
        }
        catch
        {
            // Default Console input может отсутствовать.
        }

        try
        {
            using var device =
                enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture,
                    Role.Communications);

            communicationsId = device.ID;
        }
        catch
        {
            // Default Communications input может отсутствовать.
        }

        var result =
            new List<AudioInputDeviceInfo>();

        var devices =
            enumerator.EnumerateAudioEndPoints(
                DataFlow.Capture,
                DeviceState.Active);

        foreach (var device in devices)
        {
            result.Add(
                new AudioInputDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    StringComparer.OrdinalIgnoreCase.Equals(
                        device.ID,
                        consoleId),
                    StringComparer.OrdinalIgnoreCase.Equals(
                        device.ID,
                        communicationsId)));
        }

        return result;
    }

    public static string CreateDiagnosticReport()
    {
        var devices =
            GetActiveCaptureDevices();

        var sb = new StringBuilder();

        sb.AppendLine(
            "E.D.I.T.H. AUDIO DEVICE REPORT");

        sb.AppendLine(
            $"Generated: {DateTimeOffset.Now:O}");

        sb.AppendLine();

        if (devices.Count == 0)
        {
            sb.AppendLine(
                "NO ACTIVE CAPTURE DEVICES FOUND");

            return sb.ToString();
        }

        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];

            sb.AppendLine(
                $"[{i + 1}] {device.Name}");

            sb.AppendLine(
                $"    ID: {device.Id}");

            sb.AppendLine(
                $"    Default Console: " +
                $"{device.IsDefaultConsole}");

            sb.AppendLine(
                $"    Default Communications: " +
                $"{device.IsDefaultCommunications}");

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
