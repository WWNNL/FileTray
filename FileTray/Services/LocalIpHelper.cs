using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FileTray.Services;

public static class LocalIpHelper
{
    /// <summary>猜测本机在局域网中的 IPv4 地址(通过 UDP connect 探测默认路由,不真正发包)。</summary>
    public static string GetBestLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address)) return ep.Address.ToString();
        }
        catch
        {
            // 无默认路由时退回网卡枚举
        }

        try
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address));
            if (address != null) return address.Address.ToString();
        }
        catch
        {
            // 忽略
        }

        return "127.0.0.1";
    }
}
