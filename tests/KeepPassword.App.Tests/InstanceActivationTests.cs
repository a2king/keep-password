using KeepPassword.App.Services;

namespace KeepPassword.App.Tests;

public class InstanceActivationTests
{
    [Fact]
    public void UnixSocket_IsPerUser_AndShortEnoughForMacOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = InstanceActivation.UnixSocketPath();
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime))
        {
            var directory = Path.GetDirectoryName(path)!;
            Assert.StartsWith("kp-", Path.GetFileName(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
        else
        {
            Assert.StartsWith(runtime, path);
        }

        // 不能再是所有用户共用的 /tmp/keep-password-activate.sock。
        Assert.NotEqual(Path.Combine(Path.GetTempPath(), "keep-password-activate.sock"), path);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(path) < 104, path);
    }
}
