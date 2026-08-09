using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ProsekaTools.Avalonia.Services;

namespace ProsekaTools.Avalonia.Views;

public sealed partial class SuiteUploadView : UserControl
{
    private string? _selectedFile;
    private static readonly Uri WarmupUri = new("http://go.mikuware.top/");
    private static readonly Uri ApiBaseUri = new("http://101.34.19.31:5225");
    private const string UploadPath = "/uploadTwSuite";
    private const string FormFieldName = "files";

    public SuiteUploadView()
    {
        InitializeComponent();

        ChooseFileButton.Click += async (_, _) => await ChooseFileAsync();
        UploadButton.Click += async (_, _) => await UploadAsync();
    }

    private async Task ChooseFileAsync()
    {
        if (UseLatestCheckBox.IsChecked == true)
        {
            var latest = TryGetLatestSuiteCapture();
            if (latest is null)
            {
                SetStatus("未找到 suite 捕获文件。", true);
                return;
            }
            SetSelectedFile(latest, true);
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            SetStatus("无法打开文件选择器。", true);
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Binary") { Patterns = new[] { "*.bin" } }
            }
        });

        var file = files.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SetSelectedFile(path);
        }
    }

    private async Task UploadAsync()
    {
        try
        {
            UploadButton.IsEnabled = false;
            SetStatus("准备上传...");

            var path = _selectedFile;
            if (UseLatestCheckBox.IsChecked == true || string.IsNullOrWhiteSpace(path))
            {
                path = TryGetLatestSuiteCapture();
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SetStatus("未选择有效文件。", true);
                return;
            }

            var result = await UploadFileAsync(path);
            SetStatus($"上传成功: {result}");
        }
        catch (Exception ex)
        {
            SetStatus($"上传失败: {ex.Message}", true);
        }
        finally
        {
            UploadButton.IsEnabled = true;
        }
    }

    private void SetSelectedFile(string path, bool fromLatest = false)
    {
        _selectedFile = path;
        SelectedFileText.Text = fromLatest ? $"最新: {Path.GetFileName(path)}" : Path.GetFileName(path);
        SetStatus($"已选择文件: {Path.GetFileName(path)}");
    }

    private string? TryGetLatestSuiteCapture()
    {
        try
        {
            var dir = AppPaths.CapturesSuiteDir;
            if (!Directory.Exists(dir)) return null;
            var latest = Directory.GetFiles(dir, "*.bin")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
            return latest;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> UploadFileAsync(string filePath)
    {
        var cookieContainer = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        using var client = new HttpClient(handler) { BaseAddress = ApiBaseUri };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        try { client.DefaultRequestHeaders.Add("Origin", "http://go.mikuware.top"); } catch { }
        client.DefaultRequestHeaders.Referrer = WarmupUri;
        client.Timeout = TimeSpan.FromSeconds(90);

        try { _ = await client.GetAsync(WarmupUri); } catch { }

        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, FormFieldName, Path.GetFileName(filePath));
        var uploadTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
        form.Add(new StringContent(uploadTime), "uploadtime");

        var resp = await client.PostAsync(UploadPath, form);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception($"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}\n{body}");
        }
        return body;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? global::Avalonia.Media.Brushes.IndianRed : global::Avalonia.Media.Brushes.Black;
    }
}
