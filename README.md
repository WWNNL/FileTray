# FileTray

一个借鉴 [LocalSend](https://github.com/localsend/localsend) 的局域网文件托盘(Windows / Avalonia / .NET 9)。

在同一路由器下的设备之间:

- **互相发现**:照搬 LocalSend v2 的 UDP 多播发现(组播组 `224.0.0.167`,端口 `53317`),周期心跳广播 + 即时回应;心跳报文携带节点 ID(指纹)、昵称与维护的房间列表,用于发现节点、ID 更新和房间互通
- **互传文本**:实现 LocalSend v2 的 REST 传输流程(`prepare-upload` → `upload`),文本作为 `text/plain` 传输
- **延迟检测**:每 5 秒对所有已发现节点做 HTTP ping,往返耗时即该节点延迟(设备列表与房间成员列表实时显示)
- **分布式房间(无房主,只有节点)**:
  - 8 位随机房间码(大写字母 + 数字);创建与加入是同一件事——把房间码加入本地维护列表
  - 房间**保存在本机**(`rooms.json`),即使完全离线、连不上任何节点也不会丢失,**只有手动删除才会移除**
  - 托盘只记录文件来自哪位成员、以及文件在该成员机器上的路径,文件本体不搬家,需要时再从所有者下载(仅允许下载本机登记过的路径)
  - 托盘状态(条目 + 删除墓碑)通过节点间**全量状态交换(gossip 反熵)**收敛:每 3 秒与所有宣告同一房间码的在线节点互相 POST 完整状态
  - **删除靠墓碑传播**:墓碑对同 Id 条目永久生效,离线节点迟到的旧数据不会"复活"已删除内容
  - 房间页展示成员列表(含 IP 与延迟),选中某成员则只查看该成员放入的文件

## 运行

```bash
dotnet run --project FileTray
```

首次运行需要允许防火墙放行(入站 TCP/UDP 53317)。数据(设置、房间、日志)存于 `%APPDATA%\FileTray`。

## 使用

1. **附近设备**页:等待附近节点出现(含延迟),选中后可互发文本
2. **房间**页:
   - "创建房间"生成随机房间码,或输入 8 位房间码"加入房间"(两者等价)
   - 左侧房间列表显示每个房间的文件数与在线节点数;右侧是房间详情
   - 成员列表默认"全部成员",选中某成员即筛选出只属于 TA 的文件
   - "添加文件"把本地文件放入共享托盘;"删除"对全体节点同步生效;"下载"从所有者机器拉取本体
   - "删除房间"仅从本机移除该房间,其他节点不受影响

## 命令行参数(联调/自动化用)

```
--alias NAME          指定昵称
--data-dir PATH       覆盖数据目录
--create-room CODE    启动后在本地维护指定房间码
--join-room CODE      同上(加入 = 本地维护同一房间码)
--add-file PATH       维护房间后把文件放入托盘(可多次)
```

同一台机器双实例联调示例:

```bash
./FileTray/bin/Debug/net9.0/FileTray.exe --alias NodeA --data-dir D:/t/a --create-room TEST0001
./FileTray/bin/Debug/net9.0/FileTray.exe --alias NodeB --data-dir D:/t/b --join-room TEST0001 --add-file C:/some/file.txt
```

(端口被占时自动尝试后续端口:53317 起 +20 范围)

## 与 LocalSend 协议的关系

实现了 LocalSend Protocol v2 的兼容子集,可被标准 LocalSend 客户端当作普通设备发现并互传:

| 路由 | 说明 |
|------|------|
| `GET  /api/localsend/v2/info` | 设备信息 |
| `POST /api/localsend/v2/register` | 注册(legacy 发现模式) |
| `POST /api/localsend/v2/prepare-upload` | 传输前元数据协商(MVP 一律接受) |
| `POST /api/localsend/v2/upload` | 上传文件/文本本体 |
| `POST /api/localsend/v2/cancel` | 取消会话 |

与 LocalSend 的差异(MVP 取舍):

- 仅 HTTP,未实现 LocalSend 的 HTTPS + 自签证书加密
- 接收方不做确认弹窗,PIN / sha256 校验等未实现
- 多播心跳报文额外携带 `app: "filetray"` 与 `rooms: [...]` 字段(标准客户端会忽略未知字段)

### FileTray 分布式房间 API

| 路由 | 说明 |
|------|------|
| `GET  /api/filetray/v1/ping` | 延迟检测端点,立即返回 |
| `POST /api/filetray/v1/room/sync` | 节点间全量状态交换(条目 + 墓碑),合并对方状态并返回自己的完整状态 |
| `GET  /api/filetray/v1/room/{code}` | 本机维护的房间完整状态(调试/联调) |
| `GET  /api/filetray/v1/file?path=&code=` | 从所有者机器下载托盘内登记的文件(校验路径确为本机登记) |

同步协议要点:

- **发现**:节点每 2 秒多播心跳(携带房间码列表);每 12 秒未见即判离线
- **同步**:每 3 秒一轮,对每个房间向所有宣告该房间码的在线节点 POST 全量状态;对方合并后回传自己的全量状态,一轮双向收敛
- **冲突**:新增按 `AddedAt` 后写胜;删除按墓碑 `DeletedAt` 后写胜
- **防复活**:条目被墓碑后永久不可见、不可下载、不可被远端旧状态重新引入
- **持久化**:房间 + 条目 + 墓碑防抖落盘(`rooms.json`),重启即恢复

## 项目结构

```
FileTray/
├── Models/               # DTO(LocalSend v2 报文 + 房间同步报文)与领域模型
├── Services/
│   ├── DiscoveryService.cs    # UDP 多播心跳(LocalSend 兼容 + 房间码列表)
│   ├── HttpApiService.cs      # Kestrel 服务端(LocalSend v2 + 分布式房间 API)
│   ├── RoomService.cs         # 房间/托盘本地状态、gossip 合并、墓碑、持久化
│   ├── LatencyService.cs      # HTTP ping 延迟检测
│   ├── TransferService.cs     # 发送端(prepare-upload → upload)
│   ├── SettingsService.cs     # 别名/指纹/端口持久化
│   └── ...
├── ViewModels/            # MVVM(CommunityToolkit.Mvvm)
└── Views/                 # Avalonia UI
```

## MVP 范围之外(后续可做)

- HTTPS/自签证书加密与指纹信任(LocalSend 的完整安全模型)
- 墓碑压缩(GC):墓碑永久保留防止复活,长期运行可加"全节点确认后清理"策略
- 节点别名/ID 变更通知;托盘文件本体不存在的校验提示
- 系统托盘常驻(`--hidden` 启动)、开机自启
