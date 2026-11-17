using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EmailAnalyser.Core;

public interface IServerIdentityCheck
{
    /// <summary>Null when the model server is the expected program; otherwise the reason not to send it messages.</summary>
    string? Problem();
}

/// <summary>
/// Checks that every listener on the model server's loopback port is the expected
/// executable (R17: another local program could take the port and receive message
/// content). Checked before each batch; fails closed.
/// </summary>
public sealed class LoopbackPortOwnerCheck(int port, string expectedExecutable) : IServerIdentityCheck
{
    public static string DefaultLmStudioExecutable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LM Studio", "LM Studio.exe");

    public string? Problem()
    {
        IReadOnlyList<int> owners;
        try
        {
            owners = ListeningProcessIds(port);
        }
        catch (Win32Exception)
        {
            return $"Portin {port} omistajaa ei voitu selvittää.";
        }

        if (owners.Count == 0)
        {
            return $"Porttia {port} ei kuuntele mikään ohjelma (LM Studio ei ole käynnissä).";
        }

        foreach (var pid in owners.Distinct())
        {
            string? path, name;
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException)
            {
                return $"Portin {port} omistajaa (prosessi {pid}) ei voitu tarkistaa.";
            }

            if (path is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
            {
                return $"Porttia {port} kuuntelee {name}, ei odotettu LM Studio ({expectedExecutable}).";
            }
        }

        return null;
    }

    /// <summary>Owning process ids of TCP listeners on the port, IPv4 and IPv6.</summary>
    public static IReadOnlyList<int> ListeningProcessIds(int port)
    {
        var result = new List<int>();
        Collect(AfInet, rowSize: 24, portOffset: 8, pidOffset: 20, port, result);
        Collect(AfInet6, rowSize: 56, portOffset: 20, pidOffset: 52, port, result);
        return result;
    }

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    private static void Collect(int family, int rowSize, int portOffset, int pidOffset, int port, List<int> result)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var error = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (error == 122) // ERROR_INSUFFICIENT_BUFFER: the table grew, retry with the new size
                {
                    continue;
                }

                if (error != 0)
                {
                    throw new Win32Exception(error);
                }

                var rows = Marshal.ReadInt32(buffer);
                for (var i = 0; i < rows; i++)
                {
                    var row = buffer + 4 + i * rowSize;
                    var raw = Marshal.ReadInt32(row + portOffset);
                    var localPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    if (localPort == port)
                    {
                        result.Add(Marshal.ReadInt32(row + pidOffset));
                    }
                }

                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new Win32Exception(122);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
}
