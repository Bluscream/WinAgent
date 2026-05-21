using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Management;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAgent.Models;

namespace WinAgent.Utils
{
    public static class SystemHelper
    {
        private const int SM_CLEANBOOT = 67;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int smIndex);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern IntPtr WTSOpenServer([MarshalAs(UnmanagedType.LPStr)] String pServerName);

        [DllImport("wtsapi32.dll")]
        static extern void WTSCloseServer(IntPtr hServer);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSEnumerateSessions(
            IntPtr hServer,
            int Reserved,
            int Version,
            ref IntPtr ppSessionInfo,
            ref int pCount);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("Wtsapi32.dll")]
        static extern bool WTSQuerySessionInformation(
            IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out System.IntPtr ppBuffer, out uint pBytesReturned);

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public Int32 SessionID;
            [MarshalAs(UnmanagedType.LPStr)]
            public String pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        private enum WTS_INFO_CLASS
        {
            WTSInitialProgram,
            WTSApplicationName,
            WTSWorkingDirectory,
            WTSOEMId,
            WTSSessionId,
            WTSUserName,
            WTSWinStationName,
            WTSDomainName,
            WTSConnectState,
            WTSClientBuildNumber,
            WTSClientName,
            WTSClientDirectory,
            WTSClientProductId,
            WTSClientHardwareId,
            WTSClientAddress,
            WTSClientDisplay,
            WTSClientProtocolType
        }

        private enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive,
            WTSConnected,
            WTSConnectQuery,
            WTSShadow,
            WTSDisconnected,
            WTSIdle,
            WTSListen,
            WTSReset,
            WTSDown,
            WTSInit
        }

        public static bool IsSafeMode()
        {
            return GetSystemMetrics(SM_CLEANBOOT) != 0;
        }

        public static bool IsUserLoggedIn()
        {
            return GetLoggedInUsers().Count > 0;
        }

        public static bool IsLocked()
        {
            IntPtr serverHandle = IntPtr.Zero;
            IntPtr sessionInfoPtr = IntPtr.Zero;
            int sessionCount = 0;

            if (WTSEnumerateSessions(serverHandle, 0, 1, ref sessionInfoPtr, ref sessionCount))
            {
                int dataSize = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                IntPtr currentSession = sessionInfoPtr;

                for (int i = 0; i < sessionCount; i++)
                {
                    WTS_SESSION_INFO si = (WTS_SESSION_INFO)Marshal.PtrToStructure(currentSession, typeof(WTS_SESSION_INFO))!;
                    if (si.SessionID == 1 || si.pWinStationName.Contains("Console", StringComparison.OrdinalIgnoreCase))
                    {
                        if (si.State == WTS_CONNECTSTATE_CLASS.WTSDisconnected)
                        {
                            WTSFreeMemory(sessionInfoPtr);
                            return true;
                        }
                    }
                    currentSession += dataSize;
                }
                WTSFreeMemory(sessionInfoPtr);
            }
            return false;
        }

        public static List<string> GetLoggedInUsers()
        {
            List<string> users = new List<string>();
            IntPtr serverHandle = IntPtr.Zero;
            IntPtr sessionInfoPtr = IntPtr.Zero;
            int sessionCount = 0;

            try
            {
                if (WTSEnumerateSessions(serverHandle, 0, 1, ref sessionInfoPtr, ref sessionCount))
                {
                    int dataSize = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                    IntPtr currentSession = sessionInfoPtr;

                    for (int i = 0; i < sessionCount; i++)
                    {
                        WTS_SESSION_INFO si = (WTS_SESSION_INFO)Marshal.PtrToStructure(currentSession, typeof(WTS_SESSION_INFO))!;
                        currentSession += dataSize;

                        if (si.State == WTS_CONNECTSTATE_CLASS.WTSActive || si.State == WTS_CONNECTSTATE_CLASS.WTSDisconnected)
                        {
                            IntPtr buffer = IntPtr.Zero;
                            uint bytesReturned = 0;
                            if (WTSQuerySessionInformation(serverHandle, si.SessionID, WTS_INFO_CLASS.WTSUserName, out buffer, out bytesReturned))
                            {
                                string? userName = Marshal.PtrToStringAnsi(buffer);
                                WTSFreeMemory(buffer);

                                if (!string.IsNullOrEmpty(userName) && userName != "SYSTEM" && userName != "LOCAL SERVICE" && userName != "NETWORK SERVICE")
                                {
                                    users.Add(userName);
                                }
                            }
                        }
                    }
                    WTSFreeMemory(sessionInfoPtr);
                }
            }
            catch { }
            return users;
        }

        public static HashSet<IntPtr> FlashingWindows { get; } = new HashSet<IntPtr>();
        
        public static WinAgent.Models.NeedsAttentionInfo? GetNeedsAttentionInfo()
        {
            // 0. Check for Secure Desktop (UAC / Login Screen)
            string desktop = GetActiveDesktopName();
            if (desktop == "Winlogon")
            {
                return new NeedsAttentionInfo
                {
                    ProcessName = "csrss",
                    WindowName = "Secure Desktop (UAC/Login)",
                    ClassName = "Winlogon",
                    CommandLine = "Input Desktop: Winlogon"
                };
            }

            // 1. Check for known security/UAC processes first
            var securityProcesses = new[] { "consent", "CredentialUIBroker" };
            foreach (var pName in securityProcesses)
            {
                try
                {
                    var processes = Process.GetProcessesByName(pName);
                    if (processes.Length > 0)
                    {
                        var p = processes[0];
                        return new NeedsAttentionInfo
                        {
                            ProcessName = p.ProcessName,
                            ProcessId = p.Id,
                            WindowName = pName == "consent" ? "User Account Control" : "Windows Security",
                            ClassName = "SecurityPrompt",
                            CommandLine = p.TryGetCommandLine(out var cmd) ? cmd : null
                        };
                    }
                }
                catch { }
            }

            WinAgent.Models.NeedsAttentionInfo? info = null;
            if (FlashingWindows.Count > 0)
            {
                var toRemove = new List<IntPtr>();
                foreach (var fw in FlashingWindows)
                {
                    if (!fw.IsVisible()) toRemove.Add(fw);
                    else if (info == null) info = GetWindowInfo(fw);
                }
                foreach (var r in toRemove) FlashingWindows.Remove(r);
            }

            if (info != null) return info;

            try
            {
                NativeMethods.EnumWindows((hWnd, lParam) =>
                {
                    if (hWnd.IsVisible())
                    {
                        string className = hWnd.GetWindowClassName();
                        if (className == "#32770" || className == "Credential Dialog Xaml Host")
                        {
                            info = GetWindowInfo(hWnd);
                            info.ClassName = className;
                            return false;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return info;
        }

        public static WinAgent.Models.NeedsAttentionInfo GetWindowInfo(IntPtr hWnd)
        {
            var info = new WinAgent.Models.NeedsAttentionInfo();
            info.WindowName = hWnd.GetWindowTitle();
            info.ClassName = hWnd.GetWindowClassName();

            if (hWnd.TryGetProcess(out var process) && process != null)
            {
                info.ProcessName = process.ProcessName;
                info.ProcessId = process.Id;
                if (process.TryGetCommandLine(out var cmdLine)) info.CommandLine = cmdLine;
            }
            else
            {
                info.ProcessName = "Unknown";
            }
            return info;
        }

        public static string GetActiveDesktopName()
        {
            IntPtr hDesktop = NativeMethods.OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
            if (hDesktop == IntPtr.Zero) return "Unknown";

            try
            {
                uint needed;
                NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, null!, 0, out needed);
                if (needed > 0)
                {
                    byte[] buffer = new byte[needed];
                    if (NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, buffer, needed, out _))
                    {
                        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
                    }
                }
            }
            finally
            {
                NativeMethods.CloseDesktop(hDesktop);
            }
            return "Unknown";
        }

        public static bool IsNeedsAttention() => GetNeedsAttentionInfo() != null;

        public static bool SetPnpDeviceState(string instanceId, bool enable, out string errorMessage)
        {
            errorMessage = "";
            try
            {
                var device = Nefarius.Utilities.DeviceManagement.PnP.PnPDevice.GetDeviceByInstanceId(instanceId);
                if (enable)
                {
                    device.Enable();
                }
                else
                {
                    device.Disable();
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static List<string> GetActiveIpAddresses()
        {
            var ips = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && 
                        ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    {
                        foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                ips.Add(ip.Address.ToString());
                            }
                        }
                    }
                }
            }
            catch { }
            return ips;
        }

        public static JsonNode? CleanJsonNode(JsonNode? node)
        {
            if (node == null) return null;

            if (node is JsonObject obj)
            {
                var cleanedObj = new JsonObject();
                foreach (var kv in obj)
                {
                    var cleanedVal = CleanJsonNode(kv.Value);
                    if (cleanedVal != null)
                    {
                        if (cleanedVal is JsonObject nestedObj && nestedObj.Count == 0) continue;
                        if (cleanedVal is JsonArray nestedArr && nestedArr.Count == 0) continue;

                        cleanedObj.Add(kv.Key, cleanedVal.DeepClone());
                    }
                }
                return cleanedObj.Count > 0 ? cleanedObj : null;
            }
            else if (node is JsonArray arr)
            {
                var cleanedArr = new JsonArray();
                foreach (var item in arr)
                {
                    var cleanedVal = CleanJsonNode(item);
                    if (cleanedVal != null)
                    {
                        if (cleanedVal is JsonObject nestedObj && nestedObj.Count == 0) continue;
                        if (cleanedVal is JsonArray nestedArr && nestedArr.Count == 0) continue;

                        cleanedArr.Add(cleanedVal.DeepClone());
                    }
                }
                return cleanedArr.Count > 0 ? cleanedArr : null;
            }
            else if (node is JsonValue val)
            {
                var element = val.GetValue<object>();
                if (element == null) return null;
                if (element is string s && string.IsNullOrEmpty(s)) return null;
                return val.DeepClone();
            }

            return null;
        }

        public static string BuildAndCleanEventPayload(string? eventText, string? eventType, JsonNode? additionalAttributes)
        {
            var machineObj = new JsonObject
            {
                ["name"] = Global.MachineName,
                ["ips"] = new JsonArray(GetActiveIpAddresses().Select(ip => JsonValue.Create(ip)!).ToArray())
            };

            var activeUsers = GetLoggedInUsers();
            var userName = activeUsers.FirstOrDefault() ?? Environment.UserName;
            var userObj = new JsonObject
            {
                ["name"] = userName
            };

            var eventObj = new JsonObject
            {
                ["type"] = eventType,
                ["text"] = eventText,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            if (additionalAttributes != null)
            {
                if (additionalAttributes is JsonObject additionalObj)
                {
                    foreach (var kv in additionalObj)
                    {
                        var key = kv.Key;
                        if (key == "event" || key == "event_type" || key == "machine_name" || key == "timestamp" || key == "text" || key == "type")
                        {
                            continue;
                        }

                        if (kv.Value != null)
                        {
                            eventObj[key] = kv.Value.DeepClone();
                        }
                    }
                }
            }

            var root = new JsonObject
            {
                ["machine"] = machineObj,
                ["user"] = userObj,
                ["event"] = eventObj
            };

            var cleaned = CleanJsonNode(root);
            return cleaned?.ToJsonString() ?? "{}";
        }

        public static bool? ParseState(object? state, bool currentVal)
        {
            if (state == null)
            {
                return null;
            }

            if (state is JsonElement jsonElem)
            {
                switch (jsonElem.ValueKind)
                {
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Number:
                        if (jsonElem.TryGetInt32(out int intVal))
                        {
                            return intVal != 0;
                        }
                        if (jsonElem.TryGetDouble(out double doubleVal))
                        {
                            return doubleVal != 0.0;
                        }
                        return null;
                    case JsonValueKind.String:
                        state = jsonElem.GetString();
                        break;
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        return null;
                    default:
                        return null;
                }
            }

            if (state is bool b)
            {
                return b;
            }

            if (state is int i)
            {
                return i != 0;
            }
            
            if (state is double d)
            {
                return d != 0.0;
            }

            if (state is string s)
            {
                s = s.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(s))
                {
                    return null;
                }

                if (s == "true" || s == "on" || s == "1")
                {
                    return true;
                }
                if (s == "false" || s == "off" || s == "0")
                {
                    return false;
                }
                if (s == "toggle")
                {
                    return !currentVal;
                }
            }

            return null;
        }
    }
}
