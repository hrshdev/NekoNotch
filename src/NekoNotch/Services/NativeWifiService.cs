using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace NekoNotch.Services;

/// <summary>
/// Provides native Windows Wi-Fi profile management: list saved networks,
/// identify connected network, connect, disconnect, and forget (delete) profiles.
/// </summary>
public static class NativeWifiService
{
    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(
        uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(
        IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanGetProfileList(
        IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved, out IntPtr ppProfileList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanDeleteProfile(
        IntPtr hClientHandle, ref Guid pInterfaceGuid, string strProfileName, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanDisconnect(
        IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WlanConnect(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        ref WLAN_CONNECTION_PARAMETERS pConnectionParameters,
        IntPtr pReserved);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public int isState;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_PROFILE_INFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public int wlanConnectionMode; // 0 = wlan_connection_mode_profile
        public string strProfile;
        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public int dot11BssType; // 3 = dot11_BSS_type_any
        public uint dwFlags;
    }

    /// <summary>
    /// Gets the SSID of the currently connected active Wi-Fi profile, or null if disconnected.
    /// </summary>
    public static string? GetConnectedSsid()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile != null && profile.IsWlanConnectionProfile)
            {
                return profile.ProfileName;
            }
            // Fallback for general internet profile
            return profile?.ProfileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the list of saved Wi-Fi profile names configured on the machine.
    /// </summary>
    public static List<string> GetSavedProfiles()
    {
        var list = new List<string>();
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0 || handle == IntPtr.Zero)
                return list;

            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var ifListPtr) != 0 || ifListPtr == IntPtr.Zero)
                return list;

            try
            {
                int count = Marshal.ReadInt32(ifListPtr);
                if (count == 0) return list;

                IntPtr pInfo = new(ifListPtr.ToInt64() + 8);
                var info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(pInfo, typeof(WLAN_INTERFACE_INFO))!;
                var ifGuid = info.InterfaceGuid;

                if (WlanGetProfileList(handle, ref ifGuid, IntPtr.Zero, out var pProfList) == 0 && pProfList != IntPtr.Zero)
                {
                    try
                    {
                        int profCount = Marshal.ReadInt32(pProfList);
                        IntPtr current = new(pProfList.ToInt64() + 8);
                        int size = Marshal.SizeOf(typeof(WLAN_PROFILE_INFO));

                        for (int i = 0; i < profCount; i++)
                        {
                            var prof = (WLAN_PROFILE_INFO)Marshal.PtrToStructure(current, typeof(WLAN_PROFILE_INFO))!;
                            if (!string.IsNullOrWhiteSpace(prof.strProfileName))
                            {
                                list.Add(prof.strProfileName);
                            }
                            current = new(current.ToInt64() + size);
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(pProfList);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(ifListPtr);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
        return list;
    }

    /// <summary>
    /// Connects to a saved Wi-Fi profile.
    /// </summary>
    public static bool Connect(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return false;

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) == 0 && handle != IntPtr.Zero)
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var ifListPtr) == 0 && ifListPtr != IntPtr.Zero)
                {
                    try
                    {
                        int count = Marshal.ReadInt32(ifListPtr);
                        if (count > 0)
                        {
                            IntPtr pInfo = new(ifListPtr.ToInt64() + 8);
                            var info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(pInfo, typeof(WLAN_INTERFACE_INFO))!;
                            var ifGuid = info.InterfaceGuid;

                            var cp = new WLAN_CONNECTION_PARAMETERS
                            {
                                wlanConnectionMode = 0, // profile
                                strProfile = profileName,
                                pDot11Ssid = IntPtr.Zero,
                                pDesiredBssidList = IntPtr.Zero,
                                dot11BssType = 3, // any
                                dwFlags = 0
                            };

                            int r = WlanConnect(handle, ref ifGuid, ref cp, IntPtr.Zero);
                            if (r == 0) return true;
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(ifListPtr);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }

        // Fallback to netsh
        try
        {
            var p = Process.Start(new ProcessStartInfo("netsh", $"wlan connect name=\"{profileName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Disconnects the primary wireless interface from the current Wi-Fi network.
    /// </summary>
    public static bool Disconnect()
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) == 0 && handle != IntPtr.Zero)
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var ifListPtr) == 0 && ifListPtr != IntPtr.Zero)
                {
                    try
                    {
                        int count = Marshal.ReadInt32(ifListPtr);
                        if (count > 0)
                        {
                            IntPtr pInfo = new(ifListPtr.ToInt64() + 8);
                            var info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(pInfo, typeof(WLAN_INTERFACE_INFO))!;
                            var ifGuid = info.InterfaceGuid;

                            int r = WlanDisconnect(handle, ref ifGuid, IntPtr.Zero);
                            if (r == 0) return true;
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(ifListPtr);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }

        // Fallback to netsh
        try
        {
            var p = Process.Start(new ProcessStartInfo("netsh", "wlan disconnect")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Forgets (deletes) a saved Wi-Fi profile from Windows.
    /// </summary>
    public static bool ForgetProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return false;

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) == 0 && handle != IntPtr.Zero)
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var ifListPtr) == 0 && ifListPtr != IntPtr.Zero)
                {
                    try
                    {
                        int count = Marshal.ReadInt32(ifListPtr);
                        if (count > 0)
                        {
                            IntPtr pInfo = new(ifListPtr.ToInt64() + 8);
                            var info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(pInfo, typeof(WLAN_INTERFACE_INFO))!;
                            var ifGuid = info.InterfaceGuid;

                            int r = WlanDeleteProfile(handle, ref ifGuid, profileName, IntPtr.Zero);
                            if (r == 0) return true;
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(ifListPtr);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }

        // Fallback to netsh
        try
        {
            var p = Process.Start(new ProcessStartInfo("netsh", $"wlan delete profile name=\"{profileName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
