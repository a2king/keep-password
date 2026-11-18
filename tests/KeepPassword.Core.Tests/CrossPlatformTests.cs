using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

/// <summary>macOS / Linux 专属路径：快速解锁用的设备密钥。</summary>
public class CrossPlatformTests
{
    [Fact]
    public async Task DeviceKey_ConcurrentFirstCreation_AllCallersGetTheSameKey()
    {
        for (var round = 0; round < 20; round++)
        {
            var directory = Path.Combine(Path.GetTempPath(), "kp-devkey", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "device.key");
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { start.Wait(); return VaultStore.LoadOrCreateDeviceKey(path); })).ToArray();
            start.Set();
            var keys = await Task.WhenAll(tasks);
            var onDisk = await File.ReadAllBytesAsync(path);
            Assert.All(keys, key => Assert.Equal(onDisk, key));
            Assert.Equal(["device.key"], Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeviceKey_ExistingKeyIsNeverReplaced_ButACorruptOneIs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kp-devkey", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "device.key");
        var first = VaultStore.LoadOrCreateDeviceKey(path);
        Assert.Equal(first, VaultStore.LoadOrCreateDeviceKey(path));
        File.WriteAllBytes(path, [1, 2, 3]);
        var repaired = VaultStore.LoadOrCreateDeviceKey(path);
        Assert.Equal(32, repaired.Length);
        Assert.Equal(repaired, File.ReadAllBytes(path));
        Directory.Delete(directory, recursive: true);
    }
}
