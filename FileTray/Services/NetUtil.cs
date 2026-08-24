namespace FileTray.Services;

/// <summary>网络相关的通用小工具。</summary>
public static class NetUtil
{
    /// <summary>
    /// 把 IPv4 映射的 IPv6 地址(::ffff:a.b.c.d)规范化为普通 IPv4 字符串,
    /// 避免多播/HTTP 不同栈拿到的格式不一致导致比较或拼接 URL 出错。
    /// </summary>
    public static string NormalizeIp(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return ip;
        if (ip.StartsWith("::ffff:") && !ip.Contains('%'))
        {
            var mapped = ip["::ffff:".Length..];
            if (System.Net.IPAddress.TryParse(mapped, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return mapped;
        }
        if (ip.StartsWith("::1")) return "127.0.0.1";
        return ip;
    }
}
