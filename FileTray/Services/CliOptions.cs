using System.Collections.Generic;

namespace FileTray.Services;

/// <summary>
/// 命令行参数(主要用于自动化测试与本机双实例联调):
/// --alias NAME          指定昵称
/// --data-dir PATH       覆盖数据目录(默认 %APPDATA%\FileTray)
/// --create-room CODE    启动后在本地维护指定房间码(分布式:创建与加入等效)
/// --join-room CODE      同上(加入房间 = 本地维护该房间码)
/// --add-file PATH       维护房间后把文件放入托盘(可多次)
/// </summary>
public static class CliOptions
{
    public static string? Alias { get; private set; }
    public static string? DataDir { get; private set; }
    public static string? CreateRoomCode { get; private set; }
    public static string? JoinRoomCode { get; private set; }
    public static List<string> AddFiles { get; } = new();

    public static void Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--alias" when i + 1 < args.Length:
                    Alias = args[++i];
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    DataDir = args[++i];
                    break;
                case "--create-room" when i + 1 < args.Length:
                    CreateRoomCode = args[++i].ToUpperInvariant();
                    break;
                case "--join-room" when i + 1 < args.Length:
                    JoinRoomCode = args[++i].ToUpperInvariant();
                    break;
                case "--add-file" when i + 1 < args.Length:
                    AddFiles.Add(args[++i]);
                    break;
            }
        }
    }
}
