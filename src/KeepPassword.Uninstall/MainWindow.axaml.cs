using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KeepPassword.Core.Install;

namespace KeepPassword.Uninstall;

public partial class MainWindow : Window
{
    private readonly string _installRoot;

    public MainWindow()
    {
        InitializeComponent();
        _installRoot = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        PathText.Text = "安装目录：" + _installRoot;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        UninstallButton.IsEnabled = false;
        var deleteCache = DeleteCacheBox.IsChecked == true;
        try
        {
            await Task.Run(() => RunFromTemp(_installRoot, deleteCache));
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
            UninstallButton.IsEnabled = true;
        }
    }

    private static void RunFromTemp(string installRoot, bool deleteCache)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !File.Exists(self))
        {
            InstallOperations.Uninstall(installRoot, deleteCache);
            return;
        }

        // 单文件卸载程序先复制到临时目录再启动，避免删掉正在运行的自身时失败。
        var temp = Path.Combine(Path.GetTempPath(), "keep-password-uninstall-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(temp);
        var runner = Path.Combine(temp, Path.GetFileName(self));
        File.Copy(self, runner, overwrite: true);
        var args = "--run-uninstall " + Quote(installRoot) + (deleteCache ? " --delete-cache" : "");
        Process.Start(new ProcessStartInfo
        {
            FileName = runner,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
