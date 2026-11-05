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
        DataPathText.Text = SafeDataDirectory();
        ShowRunningNotice();
    }

    private static string SafeDataDirectory()
    {
        try
        {
            return InstallOperations.DataDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "（无法读取，可在设置里查看）";
        }
    }

    /// <summary>程序还开着时提前说明：点卸载会先退出它，而不是等到文件被占用才报错。</summary>
    private void ShowRunningNotice()
    {
        var running = InstallOperations.FindRunningProductProcesses(_installRoot);
        var count = running.Count;
        foreach (var process in running)
        {
            process.Dispose();
        }

        RunningNotice.IsVisible = count > 0;
        RunningText.Text = "Keep Password 仍在运行。点击「卸载」会先退出它（未保存的编辑将丢失），再继续卸载。";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    // 删除数据是唯一无法撤销的操作：勾选后要再确认一次，没确认前"卸载"按钮保持禁用。
    private void OnDeleteChoiceChanged(object? sender, RoutedEventArgs e)
    {
        var deleting = DeleteCacheBox.IsChecked == true;
        DeleteConfirmPanel.IsVisible = deleting;
        if (!deleting)
        {
            DeleteConfirmBox.IsChecked = false;
        }

        UninstallButton.IsEnabled = !deleting || DeleteConfirmBox.IsChecked == true;
    }

    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        UninstallButton.IsEnabled = false;
        var deleteCache = DeleteCacheBox.IsChecked == true;
        try
        {
            await Task.Run(() =>
            {
                InstallOperations.StopRunningProduct(_installRoot);
                InstallOperations.Uninstall(_installRoot, deleteCache);
            });
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.IsVisible = true;
            UninstallButton.IsEnabled = true;
        }
    }
}
