using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using ProsekaTools.Avalonia.Services;

namespace ProsekaTools.Avalonia.Views;

public sealed partial class GrabDataView : UserControl
{
    private const int ServerPort = 8000;
    private readonly ObservableCollection<string> _logs = new();
    private readonly DispatcherTimer _timer;
    private CaptureServer? _server;
    private string _currentIp = "127.0.0.1";

    public GrabDataView()
    {
        InitializeComponent();

        LogsList.ItemsSource = _logs;
        UpdateLocalIp();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => UpdateLocalIp();
        _timer.Start();

        ServerToggle.IsCheckedChanged += async (_, _) =>
        {
            if (ServerToggle.IsChecked == true)
            {
                await StartServerAsync();
            }
            else
            {
                await StopServerAsync();
            }
        };
        OpenWebButton.Click += (_, _) => OpenServerUrl();
        OpenOutputDirButton.Click += (_, _) => OpenOutputDir();
        ClearLogsButton.Click += (_, _) => _logs.Clear();

        DetachedFromVisualTree += async (_, _) => await StopServerAsync();
    }

    private void UpdateLocalIp()
    {
        var ip = GetLocalIpAddress();
        _currentIp = ip;
        LocalIpText.Text = ip;
        ServerUrlText.Text = BuildServerUrl(ip);
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ip = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return ip?.ToString() ?? "未检测到IP";
        }
        catch
        {
            return "获取失败";
        }
    }

    private async Task StartServerAsync()
    {
        if (_server is not null) return;
        try
        {
            _server = new CaptureServer(ServerPort, () => _currentIp, AppendLog);
            await _server.StartAsync();
            ServerStatusText.Text = $"运行中: http://{_currentIp}:{ServerPort}/ (对外可访问)";
            ServerUrlText.Text = BuildServerUrl(_currentIp);
            AppendLog($"Server started on :{ServerPort}");
        }
        catch (Exception ex)
        {
            AppendLog($"Server start failed: {ex.Message}");
            ServerStatusText.Text = $"启动失败: {ex.Message}";
            ServerToggle.IsChecked = false;
            if (_server is not null)
            {
                await _server.StopAsync();
                _server = null;
            }
        }
    }

    private async Task StopServerAsync()
    {
        try
        {
            if (_server is not null)
            {
                await _server.StopAsync();
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _server = null;
            ServerStatusText.Text = "已停止";
            AppendLog("Server stopped");
        }
    }

    private static string BuildServerUrl(string ip) => $"http://{ip}:{ServerPort}/";

    private void OpenServerUrl()
    {
        var url = BuildServerUrl(_currentIp);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = url,
                UseShellExecute = true
            });
            AppendLog($"Open URL: {url}");
        }
        catch (Exception ex)
        {
            AppendLog($"Open URL failed: {ex.Message}");
        }
    }

    private void OpenOutputDir()
    {
        try
        {
            var dir = AppPaths.GetCapturesRoot();
            AppPaths.EnsureDir(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = dir,
                UseShellExecute = true
            });
            AppendLog($"Open dir: {dir}");
        }
        catch (Exception ex)
        {
            AppendLog($"Open dir failed: {ex.Message}");
        }
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var text = $"[{stamp}] {line}";
        Dispatcher.UIThread.Post(() =>
        {
            _logs.Add(text);
            if (LogsList.ItemCount > 0)
            {
                LogsList.ScrollIntoView(LogsList.ItemCount - 1);
            }
        });
    }
}
