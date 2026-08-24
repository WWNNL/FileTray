using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FileTray.Models;

namespace FileTray.Services;

/// <summary>
/// 发送端逻辑:按 LocalSend v2 流程(prepare-upload → upload)把文本当作 text/plain 文件发送。
/// </summary>
public sealed class TransferService
{
    private readonly SettingsService _settings;
    private readonly HttpApiService _server;
    private readonly Func<string?> _roomCodeProvider;

    public TransferService(SettingsService settings, HttpApiService server, Func<string?> roomCodeProvider)
    {
        _settings = settings;
        _server = server;
        _roomCodeProvider = roomCodeProvider;
    }

    public DeviceInfoDto SelfInfo() => new()
    {
        Alias = _settings.Alias,
        Version = "2.0",
        DeviceModel = Environment.OSVersion.VersionString,
        DeviceType = "desktop",
        Fingerprint = _settings.Fingerprint,
        Port = _server.Port,
        Protocol = "http",
        Download = false,
        Announce = false,
        App = "filetray",
        Room = _roomCodeProvider(),
    };

    public async Task SendTextAsync(DeviceRecord target, string text)
    {
        var baseUrl = $"{target.Protocol}://{target.Ip}:{target.Port}";
        var bytes = Encoding.UTF8.GetBytes(text);

        var request = new PrepareUploadRequestDto
        {
            Info = SelfInfo(),
            Files = new Dictionary<string, FileMetaDto>
            {
                ["text"] = new FileMetaDto { Id = "text", FileName = "Text.txt", Size = bytes.Length, FileType = "text/plain" },
            },
        };

        var response = await Http.PostJsonAsync<PrepareUploadResponseDto>($"{baseUrl}/api/localsend/v2/prepare-upload", request, 8000).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"设备 {target.Alias} 无响应");
        if (!response.Files.TryGetValue("text", out var token))
            throw new InvalidOperationException($"设备 {target.Alias} 拒绝了传输");

        var url = $"{baseUrl}/api/localsend/v2/upload?sessionId={Uri.EscapeDataString(response.SessionId)}&fileId=text&token={Uri.EscapeDataString(token)}";
        await Http.PostBytesAsync(url, bytes, 30000).ConfigureAwait(false);
    }
}
