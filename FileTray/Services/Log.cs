using System;
using System.IO;

namespace FileTray.Services;

public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static void Init(string path)
    {
        try
        {
            var file = new FileInfo(path);
            Directory.CreateDirectory(file.DirectoryName!);
            if (file.Exists && file.Length > 1024 * 1024) file.Delete();
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch
        {
            // 日志初始化失败不影响运行
        }

        Info("========== FileTray 启动 ==========");
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try { _writer?.WriteLine(line); } catch { }
        }
    }
}
