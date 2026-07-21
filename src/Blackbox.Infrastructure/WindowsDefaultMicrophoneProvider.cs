using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed class WindowsDefaultMicrophoneProvider(
    ILogger<WindowsDefaultMicrophoneProvider> logger) : IDefaultMicrophoneProvider
{
    private static readonly Guid EnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public string? GetDefaultDeviceId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(EnumeratorClassId, throwOnError: true)
                ?? throw new InvalidOperationException("Windows Core Audio is unavailable.");
            enumerator = (IMMDeviceEnumerator)(Activator.CreateInstance(enumeratorType)
                ?? throw new InvalidOperationException("Windows Core Audio could not be started."));
            var result = enumerator.GetDefaultAudioEndpoint(
                EDataFlow.Capture,
                ERole.Console,
                out device);
            Marshal.ThrowExceptionForHR(result);
            result = device.GetId(out var deviceId);
            Marshal.ThrowExceptionForHR(result);
            return deviceId;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not read the Windows default microphone.");
            return null;
        }
        finally
        {
            if (device is not null && Marshal.IsComObject(device))
            {
                _ = Marshal.FinalReleaseComObject(device);
            }

            if (enumerator is not null && Marshal.IsComObject(enumerator))
            {
                _ = Marshal.FinalReleaseComObject(enumerator);
            }
        }
    }

    private enum EDataFlow
    {
        Render,
        Capture,
        All
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, out IntPtr instance);

        [PreserveSig]
        int OpenPropertyStore(uint storageAccess, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }
}
