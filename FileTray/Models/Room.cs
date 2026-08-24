using System;
using System.Collections.Generic;

namespace FileTray.Models;

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

/// <summary>
/// 删除墓碑:分布式删除靠墓碑传播,墓碑对同 Id 条目永久生效,
/// 防止离线节点迟到的旧条目把已删除内容"复活"。
/// </summary>
public class TombstoneDto
{
    public string ItemId { get; set; } = "";
    public long DeletedAt { get; set; } // unix 毫秒
    public string DeletedBy { get; set; } = "";
}

/// <summary>
/// 节点间全量状态交换报文(gossip 反熵):
/// 请求方发本地状态,响应方合并后回自己的状态,双方一轮收敛。
/// </summary>
public class RoomSyncDto
{
    public string Code { get; set; } = "";
    public List<TrayItemDto> Items { get; set; } = new();
    public List<TombstoneDto> Tombstones { get; set; } = new();
}

/// <summary>本地房间摘要(UI 用)。</summary>
public sealed record RoomSummary(string Code, int ItemCount);
