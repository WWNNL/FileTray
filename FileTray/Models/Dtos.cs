using System.Collections.Generic;

namespace FileTray.Models;

/// <summary>
/// 设备信息,对应 LocalSend 协议 v2 的发现/注册报文。
/// 在 LocalSend 字段之外扩展了 App(标记 FileTray)与 Room(当前承载的房间码),其他客户端会忽略未知字段。
/// </summary>
public class DeviceInfoDto
{
    public string Alias { get; set; } = "";
    public string Version { get; set; } = "2.0";
    public string? DeviceModel { get; set; }
    public string? DeviceType { get; set; }
    public string Fingerprint { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "http";
    public bool Download { get; set; }
    public bool Announce { get; set; }
    public string? App { get; set; }
    public string? Room { get; set; }
}

public class FileMetaDto
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string FileType { get; set; } = "application/octet-stream";
}

public class PrepareUploadRequestDto
{
    public DeviceInfoDto? Info { get; set; } = new();
    public Dictionary<string, FileMetaDto>? Files { get; set; } = new();
}

public class PrepareUploadResponseDto
{
    public string SessionId { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new();
}
