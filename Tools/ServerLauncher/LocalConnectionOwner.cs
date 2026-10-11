using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ServerLauncher;

// Windows启动器在发送管理命令前核对服务器侧已连接socket的PID；进程存活本身不能证明端口仍归它。
internal static class LocalConnectionOwner
{
    public static bool Matches(TcpClient client, int processId)
    {
        if (processId <= 0 || client.Client.LocalEndPoint is not IPEndPoint source ||
            client.Client.RemoteEndPoint is not IPEndPoint target || !target.Address.Equals(IPAddress.Loopback))
            return false;
        int size = 0;
        if (GetExtendedTcpTable(0, ref size, false, 2, 5, 0) != 122 || size < 4 || size > 16 * 1024 * 1024)
            return false;
        nint table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 5, 0) != 0) return false;
            int count = Marshal.ReadInt32(table);
            if (count < 0 || count > (size - 4) / 24) return false;
            for (int i = 0; i < count; i++)
            {
                nint row = table + 4 + i * 24;
                if (Marshal.ReadInt32(row, 4) == 0x0100007F && Marshal.ReadInt32(row, 12) == 0x0100007F &&
                    Port(Marshal.ReadInt32(row, 8)) == target.Port && Port(Marshal.ReadInt32(row, 16)) == source.Port &&
                    Marshal.ReadInt32(row, 20) == processId)
                    return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    static int Port(int value) => ((value & 255) << 8) | ((value >> 8) & 255);
    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
}
