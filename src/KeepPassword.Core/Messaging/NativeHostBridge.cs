using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace KeepPassword.Core.Messaging;

public static class NativeHostBridge
{
    /// <summary>
    /// 处理浏览器经常驻 port 发来的一条消息：转发给桌面端，并把请求里的 requestId 原样带回，
    /// 让扩展在同一个 port 上并发多条请求时能对上回复。不会抛出异常，失败时返回 error 回复。
    /// </summary>
    public static string Relay(string requestJson, string sessionFile)
    {
        ArgumentNullException.ThrowIfNull(requestJson);
        var requestId = RequestIdOf(requestJson);
        string reply;
        try
        {
            reply = Forward(requestJson, sessionFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            reply = AutofillProtocol.Serialize(AutofillResponse.Error("无法读取本机填充通道。"));
        }

        return requestId is null ? reply : WithRequestId(reply, requestId);
    }

    /// <summary>返回 requestId 的原始 JSON 文本（数字或字符串），没有或无法解析时返回 null。</summary>
    public static string? RequestIdOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("requestId", out var id)
                && id.ValueKind is JsonValueKind.Number or JsonValueKind.String
                    ? id.GetRawText()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string WithRequestId(string replyJson, string rawRequestId)
    {
        try
        {
            using var reply = JsonDocument.Parse(replyJson);
            using var id = JsonDocument.Parse(rawRequestId);
            if (reply.RootElement.ValueKind != JsonValueKind.Object)
            {
                return replyJson;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in reply.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals("requestId"))
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WritePropertyName("requestId");
                id.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return replyJson;
        }
    }

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
            // GetAwaiter preserves SocketException instead of wrapping it in
            // AggregateException when a stale session points to a closed port.
            client.ConnectAsync(IPAddress.Loopback, channel.Port)
                .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

            client.ReceiveTimeout = 120000;
            client.SendTimeout = 15000;
            using var stream = client.GetStream();
            NativeMessageFraming.Write(stream, authorized);
            return NativeMessageFraming.Read(stream) ?? AutofillProtocol.Serialize(AutofillResponse.Error("客户端没有响应。"));
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or TimeoutException)
        {
            return AutofillProtocol.Serialize(AutofillResponse.Error("无法连接 Keep Password 客户端。"));
        }
    }
}
