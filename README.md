# FileTray

一个借鉴 [LocalSend](https://github.com/localsend/localsend) 的局域网文件托盘 MVP(Windows / Avalonia / .NET 9)。

在同一路由器下的设备之间:

- **互相发现**:照搬 LocalSend v2 的 UDP 多播发现(组播组 `224.0.0.167`,端口 `53317`),周期广播 + 即时回应
- **互传文本**:实现 LocalSend v2 的 REST 传输流程(`prepare-upload` → `upload`),文本作为 `text/plain` 传输
- **局域网房间**:创建者生成 8 位随机房间码(大写字母 + 数字),加入同一房间码的设备共享一个**托盘列表**:
  - 托盘只记录文件来自哪位成员、以及文件在该成员机器上的路径,**文件本体不搬家**
  - 任何成员"传入 / 删除"操作都会实时同步到所有成员(房主权威模式:操作提交给房主,房主全量广播)
  - 需要文件时再从所有者机器按需下载(仅允许下载托盘内登记过的路径)

## 运行

```bash
dotnet run --project FileTray
```

首次运行需要允许防火墙放行(入站 TCP/UDP 53317)。数据(设置、日志)存于 `%APPDATA%\FileTray`。

## 使用

1. **附近设备**页:等待附近的 FileTray 出现,选中后输入文本发送/接收
2. **房间**页:
   - 一台设备点"创建房间",生成房间码(可复制)
   - 其他设备输入 8 位房间码加入
   - 房间内任何成员"添加文件"把本地文件放入共享托盘,所有人立刻看到
   - "删除"对全体同步生效;"下载"从文件所有者机器拉取本体(Windows 上会弹保存对话框)

## 命令行参数(联调/自动化用)

```
--alias NAME          指定昵称
--data-dir PATH       覆盖数据目录
--create-room CODE    启动后以指定房间码创建房间
--join-room CODE      启动后加入指定房间
--add-file PATH       加入房间后放入托盘(可多次)
```

同一台机器双实例联调示例:

```bash
./FileTray/bin/Debug/net9.0/FileTray.exe --alias HostPC --data-dir D:/t/a --create-room ABCD1234
./FileTray/bin/Debug/net9.0/FileTray.exe --alias MemberPC --data-dir D:/t/b --join-room ABCD1234 --add-file C:/some/file.txt
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
- 多播报文额外携带 `app: "filetray"` 与 `room` 字段(标准客户端会忽略未知字段)

### FileTray 房间扩展 API

| 路由 | 说明 |
|------|------|
| `GET  /api/filetray/v1/room/{code}` | 查询房间(仅房主响应,用于探测谁是房主) |
| `POST /api/filetray/v1/room/join` | 加入房间 |
| `POST /api/filetray/v1/room/update` | 房主向成员推送全量房间状态(心跳,5 秒) |
| `POST /api/filetray/v1/room/tray/add` | 成员提交"放入托盘" |
| `POST /api/filetray/v1/room/tray/remove` | 成员提交"从托盘删除" |
| `POST /api/filetray/v1/room/leave` | 成员离开 |
| `GET  /api/filetray/v1/file?path=&code=` | 从所有者机器下载托盘内登记的文件 |

房间容错:

- 成员连续两个心跳周期(10 秒)无响应才被房主摘除;短暂抖动不断线
- 成员 15 秒收不到房主心跳即退出房间(日志可查);房主关闭房间会广播 `closed`
- 房主地址由"成员实际连接到的地址"修正,缓解多网卡/虚拟网卡场景

## 项目结构

```
FileTray/
├── Models/               # DTO(LocalSend v2 报文 + 房间扩展)与领域模型
├── Services/
│   ├── DiscoveryService.cs    # UDP 多播发现(LocalSend 兼容)
│   ├── HttpApiService.cs      # Kestrel 服务端(LocalSend v2 + 房间 API)
│   ├── RoomService.cs         # 房间/托盘权威状态与同步逻辑
│   ├── TransferService.cs     # 发送端(prepare-upload → upload)
│   ├── SettingsService.cs     # 别名/指纹/端口持久化
│   └── ...
├── ViewModels/            # MVVM(CommunityToolkit.Mvvm)
└── Views/                 # Avalonia UI
```

## MVP 范围之外(后续可做)

- HTTPS/自签证书加密与指纹信任(LocalSend 的完整安全模型)
- 托盘文件本体不存在时的校验/提示;删除同步时的所有权策略细化
- 系统托盘常驻(`--hidden` 启动)、开机自启
- 房主迁移(房主退出时房间解散,MVP 即如此)
