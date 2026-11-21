using System.Runtime.InteropServices;

namespace PulseBar.Utilities;

/// <summary>
/// Changes the default output device through Windows' undocumented IPolicyConfig (what Sound settings does).
/// </summary>
/// <remarks>
/// The interface isn't public, so a future Windows update may change it. No exception escapes then;
/// <see cref="TrySetDefaultEndpoint"/> returns false and the reason.
/// IPolicyConfig from Windows 7 on; if missing, the older Vista IPolicyConfigVista is tried.
/// </remarks>
internal static class AudioPolicyConfig
{
    // ERole: eConsole, eMultimedia, eCommunications; all three are set, as Windows itself does
    private static readonly int[] Roles = [0, 1, 2];

    public static bool TrySetDefaultEndpoint(string deviceId, out string? error)
    {
        object? client = null;
        try
        {
            client = new PolicyConfigClient();
            Func<int, int> setRole;
            if (client is IPolicyConfig policy)
            {
                setRole = role => policy.SetDefaultEndpoint(deviceId, role);
            }
            else if (client is IPolicyConfigVista vista)
            {
                setRole = role => vista.SetDefaultEndpoint(deviceId, role);
            }
            else
            {
                error = "PolicyConfigClient does not implement IPolicyConfig";
                return false;
            }

            foreach (int role in Roles)
            {
                int hr = setRole(role);
                if (hr < 0)
                {
                    error = $"SetDefaultEndpoint(role {role}) failed: 0x{hr:X8}";
                    return false;
                }
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // Class missing (REGDB_E_CLASSNOTREG), interface changed and so on
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (client is not null && Marshal.IsComObject(client))
            {
                Marshal.ReleaseComObject(client);
            }
        }
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    /// <summary>Windows 7 and later. Method order must match the vtable; the rest are unused.</summary>
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);

        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr format);

        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);

        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);

        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);

        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);

        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    /// <summary>The Vista version: no ResetDeviceFormat.</summary>
    [ComImport]
    [Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);

        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr format);

        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);

        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);

        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);

        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);

        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
