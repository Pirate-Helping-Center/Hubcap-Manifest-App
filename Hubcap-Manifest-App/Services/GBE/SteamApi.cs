using System;
using System.Runtime.InteropServices;

namespace HubcapManifestApp.Services.GBE
{
    public static class SteamApi
    {
        private const string SteamApiDll = "steam_api64.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDllDirectory(string? lpPathName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeLibrary(IntPtr hModule);

        [DllImport(SteamApiDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SteamAPI_InitFlat(IntPtr pOutErrMsg);

        [DllImport(SteamApiDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SteamAPI_SteamUser_v023();

        [DllImport(SteamApiDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SteamAPI_ISteamUser_RequestEncryptedAppTicket(IntPtr self, IntPtr pDataToInclude, int cbDataToInclude);

        [DllImport(SteamApiDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SteamAPI_ISteamUser_GetEncryptedAppTicket(IntPtr self, [Out] byte[] pTicket, int cbMaxTicket, out uint pcbTicket);

        [DllImport(SteamApiDll, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
    }
}
