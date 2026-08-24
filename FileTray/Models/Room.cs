using System;
using System.Collections.Generic;

namespace FileTray.Models;

public enum RoomRole
{
    None,
    Host,
    Member,
}

public class MemberDto
{
    public string Fingerprint { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>
/// 托盘条目:只记录文件来自哪位成员(指纹/别名)以及文件在该成员机器上的路径,
/// 文件本体始终留在所有者机器上,其他成员按需拉取。
/// </summary>
public class TrayItemDto
{
    public string Id { get; set; } = "";
    public string OwnerFingerprint { get; set; } = "";
    public string OwnerAlias { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public long AddedAt { get; set; } // unix 毫秒
}

public class RoomStateDto
{
    public string Code { get; set; } = "";
    public bool Closed { get; set; }
    public string HostFingerprint { get; set; } = "";
    public List<MemberDto> Members { get; set; } = new();
    public List<TrayItemDto> Tray { get; set; } = new();
}

public class RoomJoinRequestDto
{
    public string Code { get; set; } = "";
    public MemberDto? Member { get; set; }
    /// <summary>加入方实际连接到的房主 IP(用于房主修正自己在成员表里的地址)。</summary>
    public string? SeenHostIp { get; set; }
}

public class TrayAddRequestDto
{
    public string Code { get; set; } = "";
    public MemberDto? Member { get; set; }
    public TrayItemDto? Item { get; set; }
}

public class TrayRemoveRequestDto
{
    public string Code { get; set; } = "";
    public string ItemId { get; set; } = "";
}

public class RoomLeaveRequestDto
{
    public string Code { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}
