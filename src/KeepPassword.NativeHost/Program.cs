using KeepPassword.Core.Messaging;
using KeepPassword.Core.Runtime;

LibProbe.Attach();

// 扩展用 connectNative 保持一个常驻 port：宿主循环读取消息，直到浏览器关闭 stdin。
// 每条消息单独在线程池处理，慢的短密钥校验不会挡住候选查询；回复带回 requestId 供扩展配对。
// 每条消息都重新读取通道文件，桌面端重启换了端口也能继续用。
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var writeLock = new object();
var pending = new List<Task>();
var sessionFile = AutofillChannel.SessionFile();

while (true)
{
    string? message;
    try
    {
        message = NativeMessageFraming.Read(stdin);
    }
    catch (InvalidDataException)
    {
        Write(AutofillProtocol.Serialize(AutofillResponse.Error("消息长度无效。")));
        break;
    }

    if (message is null)
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(message))
    {
        continue;
    }

    pending.RemoveAll(task => task.IsCompleted);
    pending.Add(Task.Run(() =>
    {
        string reply;
        try
        {
            reply = NativeHostBridge.Relay(message, sessionFile);
        }
        catch (Exception)
        {
            var id = NativeHostBridge.RequestIdOf(message);
            var error = AutofillProtocol.Serialize(AutofillResponse.Error("本机宿主处理消息失败。"));
            reply = id is null ? error : NativeHostBridge.WithRequestId(error, id);
        }

        Write(reply);
    }));
}

// stdin 关闭后仍把已收到的请求回复完（一次性调用方式也依赖这一点），再退出。
Task.WaitAll(pending.ToArray());
return 0;

void Write(string json)
{
    lock (writeLock)
    {
        try
        {
            NativeMessageFraming.Write(stdout, json);
        }
        catch (IOException)
        {
            // 浏览器已经断开 port。
        }
    }
}
