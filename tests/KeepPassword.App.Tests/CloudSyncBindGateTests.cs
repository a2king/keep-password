using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using KeepPassword.App.Services;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Tests;

public class CloudSyncBindGateTests
{
    [AvaloniaFact]
    public async Task SecondBind_WhileFirstIsRunning_DoesNotPostAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), "kp-bind-" + Guid.NewGuid().ToString("n") + ".kpvault");
        using var session = new VaultStore(KdfProfile.Fast).CreateLocalSession(path, "short-key");
        session.SaveEncryptionKeyFile(path + ".kpkey", session.PendingEncryptionKey);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        var handler = new GateHandler(started, release, () => Interlocked.Increment(ref posts));
        var coordinator = new CloudSyncCoordinator
        {
            ServiceFactory = _ => new VaultSyncService("http://sync.test", handler)
        };
        coordinator.Attach(session);
        var first = coordinator.BindAsync("person@example.com", "website-password-12", "KPB-test-binding-key", "ABCD2345");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.BindAsync("person@example.com", "website-password-12", "KPB-test-binding-key", "ABCD2345"));
        Assert.Contains("正在绑定", busy.Message);
        Assert.Equal(1, posts);
        release.TrySetResult();
        await Assert.ThrowsAsync<SyncApiException>(() => first);
        Assert.Equal(1, posts);
        coordinator.Dispose();
    }

    private sealed class GateHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started;
        private readonly TaskCompletionSource _release;
        private readonly Action _count;

        public GateHandler(TaskCompletionSource started, TaskCompletionSource release, Action count)
        {
            _started = started;
            _release = release;
            _count = count;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/device/bind") == true)
            {
                _count();
                _started.TrySetResult();
                await _release.Task;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"邮箱或密码不正确\"}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
