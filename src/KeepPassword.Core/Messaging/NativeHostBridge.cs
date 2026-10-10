using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace KeepPassword.Core.Messaging;

public static class NativeHostBridge
{
    public static string Forward(string requestJson, string sessionFile)
    {
        ArgumentNullException.ThrowIfNull(requestJson);
        if (!AutofillChannel.TryRead(sessionFile, out var channel) || channel is null)
        {
            return AutofillProtocol.Serialize(AutofillResponse.Error("Keep Password 客户端未运行。"));
        }

        string authorized;
        try
        {
            authorized = AutofillChannel.AttachToken(requestJson, channel.Token);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return AutofillProtocol.Serialize(AutofillResponse.Error("消息无法识别。"));
        }

        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, channel.Port);
            if (!connect.Wait(TimeSpan.FromSeconds(2)))
            {
                return AutofillProtocol.Serialize(AutofillResponse.Error("Keep Password 客户端未运行。"));
            }

            client.ReceiveTimeout = 120000;
            client.SendTimeout = 15000;
            using var stream = client.GetStream();
            NativeMessageFraming.Write(stream, authorized);
            return NativeMessageFraming.Read(stream) ?? AutofillProtocol.Serialize(AutofillResponse.Error("客户端没有响应。"));
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            return AutofillProtocol.Serialize(AutofillResponse.Error("无法连接 Keep Password 客户端。"));
        }
    }
}
