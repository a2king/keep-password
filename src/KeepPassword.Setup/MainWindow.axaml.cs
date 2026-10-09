using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KeepPassword.Core.Install;

namespace KeepPassword.Setup;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PathBox.Text = InstallConstants.DefaultInstallDirectory();
        StatusText.Text = "准备安装 " + InstallConstants.ProductName + " " + InstallConstants.Version + "。";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择安装目录",
            AllowMultiple = false
        });
        if (folders.Count == 0)
        {
            return;
        }

        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            PathBox.Text = Path.Combine(path, "KeepPassword");
        }
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        var target = (PathBox.Text ?? "").Trim();
        if (target.Length == 0)
        {
            ShowError("请填写安装目录。");
            return;
        }

        InstallButton.IsEnabled = false;
        StatusText.Text = "正在安装…";
        try
        {
            await Task.Run(() =>
            {
                using var payload = OpenPayload();
                var extract = Path.Combine(Path.GetTempPath(), "keep-password-setup-" + Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(extract);
                try
                {
                    ZipFile.ExtractToDirectory(payload, extract);
                    InstallOperations.InstallFromDirectory(extract, target);
                }
                finally
                {
                    try
                    {
                        Directory.Delete(extract, recursive: true);
                    }
                    catch (Exception)
                    {
                    }
                }
            });

            StatusText.Text = "安装完成。";
            var exe = Path.Combine(target, InstallConstants.AppExecutableName());
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = target,
                    UseShellExecute = true
                });
            }

            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            StatusText.Text = "安装失败。";
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private static Stream OpenPayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("KeepPassword.Setup.payload.zip");
        if (stream is not null)
        {
            return stream;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "payload.zip");
        if (File.Exists(beside))
        {
            return File.OpenRead(beside);
        }

        throw new FileNotFoundException("安装包缺少 payload.zip。请使用官方发布的安装程序。");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
