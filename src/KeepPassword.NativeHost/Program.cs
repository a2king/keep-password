using KeepPassword.Core.Messaging;
using KeepPassword.Core.Runtime;

LibProbe.Attach();

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
string? message;
try
{
    message = NativeMessageFraming.Read(stdin);
}
catch (InvalidDataException)
{
    Write(stdout, AutofillProtocol.Serialize(AutofillResponse.Error("消息长度无效。")));
    return 1;
}

if (string.IsNullOrWhiteSpace(message))
{
    return 0;
}

Write(stdout, NativeHostBridge.Forward(message, AutofillChannel.SessionFile()));
return 0;

static void Write(Stream stdout, string json) => NativeMessageFraming.Write(stdout, json);
