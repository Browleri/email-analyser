using System.Diagnostics;
using System.Runtime.InteropServices;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace OutlookProbe;

/// <summary>
/// Attaches to an already running Outlook instance. Never starts Outlook:
/// <c>new Application()</c> would launch a closed Outlook, and .NET 5+ has no
/// <c>Marshal.GetActiveObject</c>, so the running object table is queried directly.
/// </summary>
internal static class OutlookConnection
{
    public static Outlook.Application? AttachToRunning(out string? error)
    {
        error = null;
        if (Process.GetProcessesByName("OUTLOOK").Length == 0)
        {
            error = "Outlook ei ole käynnissä. Avaa Classic Outlook ja yritä uudelleen.";
            return null;
        }

        var hr = CLSIDFromProgID("Outlook.Application", out var clsid);
        if (hr < 0)
        {
            error = $"Outlookin COM-luokkaa ei löytynyt (HRESULT 0x{hr:X8}).";
            return null;
        }

        hr = GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
        if (hr < 0 || instance is null)
        {
            error = $"Käynnissä olevaan Outlookiin ei saatu yhteyttä (HRESULT 0x{hr:X8}). " +
                    "Outlook voi olla vielä käynnistymässä, tai ohjelma ja Outlook ajetaan eri oikeustasoilla.";
            return null;
        }

        return (Outlook.Application)instance;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
}
