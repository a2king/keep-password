using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using KeepPassword.Core.Messaging;

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
string? message;
try
{
    message = NativeMessageFraming.Read(stdin);
}
catch (InvalidDataException)
{
    WriteError(stdout, "消息长度无效。");
    return 1;
}

if (string.IsNullOrWhiteSpace(message))
{
    return 0;
}

try
{
    using var client = new TcpClient();
    var connect = client.ConnectAsync(IPAddress.Loopback, NativeMessagingDefaults.Port);
    if (!connect.Wait(TimeSpan.FromSeconds(2)))
    {
        WriteError(stdout, "Keep Password 客户端未运行。");
        return 1;
    }

    client.ReceiveTimeout = 120000;
    client.SendTimeout = 15000;
    using var stream = client.GetStream();
    NativeMessageFraming.Write(stream, message);
    var response = NativeMessageFraming.Read(stream) ?? AutofillProtocol.Serialize(AutofillResponse.Error("客户端没有响应。"));
    NativeMessageFraming.Write(stdout, response);
    return 0;
}
catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
{
    WriteError(stdout, "无法连接 Keep Password 客户端。");
    return 1;
}

static void WriteError(Stream stdout, string message)
{
    var json = JsonSerializer.Serialize(new { type = "error", message });
    NativeMessageFraming.Write(stdout, json);
}
