using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ProsekaTools.Avalonia.Services;
using IOPath = System.IO.Path;

namespace ProsekaTools.Avalonia.Views;

public sealed partial class MysekaiView : UserControl
{
    private string? _selectedFile;

    private readonly Dictionary<int, MapScene> _scenes = MapScene.CreateDefaults();
    private Dictionary<int, MapData> _maps = new();
    private int _currentSiteId = 1;

    private readonly ObservableCollection<MusicRecordEntry> _musicRecords = new();
    private readonly HashSet<int> _collectedMusicRecordIds = new();
    private bool _hasCollectedMusicRecordData;

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private double _zoom = 1.0;

    static MysekaiView()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "image/webp,image/apng,image/*,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7");
        _http.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        _http.DefaultRequestHeaders.Add("Pragma", "no-cache");
    }

    public MysekaiView()
    {
        InitializeComponent();
        InitRightSideDefaults();

        MusicRecordsList.ItemsSource = _musicRecords;

        ChooseFileButton.Click += async (_, _) => await ChooseFileAsync();
        StartDecryptButton.Click += async (_, _) => await StartDecryptAsync();
        LoadLatestJsonButton.Click += async (_, _) => await LoadLatestJsonAsync();
        SelfCheckButton.Click += async (_, _) => await SelfCheckAsync();

        MapSelectCombo.SelectionChanged += (_, _) =>
        {
            if (MapSelectCombo.SelectedItem is ComboBoxItem item && item.Tag is int id)
            {
                _currentSiteId = id;
                _ = DrawCurrentMapAsync();
            }
        };
        ShowIconsCheckBox.IsCheckedChanged += (_, _) => _ = DrawCurrentMapAsync();
        ZoomInButton.Click += (_, _) => ApplyZoom(_zoom * 1.2);
        ZoomOutButton.Click += (_, _) => ApplyZoom(_zoom / 1.2);
        ResetZoomButton.Click += (_, _) => ApplyZoom(1.0);
    }

    private void InitRightSideDefaults()
    {
        MapSelectCombo.Items.Clear();
        foreach (var kv in _scenes)
        {
            MapSelectCombo.Items.Add(new ComboBoxItem { Content = $"{kv.Key}: {kv.Value.Name}", Tag = kv.Key });
        }
        MapSelectCombo.SelectedIndex = 0;
    }

    private async Task ChooseFileAsync()
    {
        if (UseLatestCheckBox.IsChecked == true)
        {
            var latest = TryGetLatestCapture();
            if (latest is null)
            {
                SetStatus("未在 captures/mysekai 找到文件。", true);
                return;
            }
            _selectedFile = latest;
            SelectedFileTextBox.Text = _selectedFile;
            SetStatus($"已选择最新: {IOPath.GetFileName(_selectedFile)}");
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
                new FilePickerFileType("Data") { Patterns = new[] { "*.bin", "*.json" } }
            }
        });

        var file = files.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _selectedFile = path;
            SelectedFileTextBox.Text = _selectedFile;
            SetStatus($"已选择: {IOPath.GetFileName(_selectedFile)}");
        }
    }

    private string? TryGetLatestCapture()
    {
        try
        {
            var dir = AppPaths.CapturesMysekaiDir;
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir)
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private string? TryGetLatestJson()
    {
        try
        {
            var dir = AppPaths.OutputMysekaiDir;
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "*.json")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task StartDecryptAsync()
    {
        try
        {
            StatusText.Text = string.Empty;

            var inputFile = _selectedFile;
            if (UseLatestCheckBox.IsChecked == true || string.IsNullOrWhiteSpace(inputFile))
            {
                inputFile = TryGetLatestCapture();
            }

            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                SetStatus("未选择有效的输入文件。", true);
                return;
            }

            var region = GetSelectedRegion();

            var outputDir = AppPaths.OutputMysekaiDir;
            Directory.CreateDirectory(outputDir);
            var outName = IOPath.GetFileNameWithoutExtension(inputFile) + ".json";
            var outputPath = IOPath.Combine(outputDir, outName);

            var (exePath, args) = BuildSssekaiCommand(inputFile, outputPath, region);
            if (exePath is null || args is null)
            {
                SetStatus("未找到内置 Python 或 sssekai 模块。请确认已内置 macOS Python 分发，并包含 sssekai。", true);
                return;
            }

            var ok = await RunProcessAsync(exePath, args);
            if (ok && File.Exists(outputPath))
            {
                SetStatus($"解密完成: {outputPath}");
                await LoadJsonAndPopulateMapsAsync(outputPath);
            }
            else
            {
                SetStatus("解密失败，请检查输入文件和 region。", true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private static (string? exePath, string? args) BuildSssekaiCommand(string inputFile, string outputPath, string region)
    {
        var pythonExe = FindBundledPython() ?? "python3";
        var args = $"-m sssekai apidecrypt \"{inputFile}\" \"{outputPath}\" --region {region}";
        return (pythonExe, args);
    }

    private static string? FindBundledPython()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            IOPath.Combine(baseDir, "python", "bin", "python3"),
            IOPath.Combine(baseDir, "python", "bin", "python"),
            IOPath.Combine(baseDir, "python", "Python.framework", "Versions", "Current", "bin", "python3")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return null;
    }

    private async Task<bool> RunProcessAsync(string exePath, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            TryApplyBundledPythonEnv(psi);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Start();

            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(stdout)) Debug.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Debug.WriteLine(stderr);

            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return false;
        }
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunProcessCaptureAsync(string exePath, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            TryApplyBundledPythonEnv(psi);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Start();
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static void TryApplyBundledPythonEnv(ProcessStartInfo psi)
    {
        var baseDir = AppContext.BaseDirectory;
        var bundledRoot = IOPath.Combine(baseDir, "python");
        if (!psi.FileName.StartsWith(bundledRoot, StringComparison.Ordinal)) return;

        psi.Environment["DYLD_FRAMEWORK_PATH"] = bundledRoot;
        psi.Environment["DYLD_LIBRARY_PATH"] = IOPath.Combine(bundledRoot, "Python.framework", "Versions", "Current", "lib");
    }

    private async Task SelfCheckAsync()
    {
        var pythonExe = FindBundledPython();
        if (pythonExe is null)
        {
            SetStatus("未找到内置 Python。请确认 python/ 目录已拷贝到输出目录。", true);
            return;
        }

        var (code, stdout, stderr) = await RunProcessCaptureAsync(
            pythonExe,
            "-c \"import sssekai; print(sssekai.__version__)\""
        );

        if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            SetStatus($"自检通过: python={pythonExe}, sssekai={stdout}");
        }
        else
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? "未知错误" : stderr;
            SetStatus($"自检失败: {msg}", true);
        }
    }

    private async Task LoadLatestJsonAsync()
    {
        var latest = TryGetLatestJson();
        if (latest == null)
        {
            SetStatus("未在 output/mysekai 找到 JSON。", true);
            return;
        }
        await LoadJsonAndPopulateMapsAsync(latest);
    }

    private async Task LoadJsonAndPopulateMapsAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("updatedResources", out var updated) ||
                !updated.TryGetProperty("userMysekaiHarvestMaps", out var mapsArray))
            {
                SetStatus("JSON 缺少 userMysekaiHarvestMaps。", true);
                return;
            }

            var region = GetSelectedRegion();
            await MysekaiMusicRecordMaster.EnsureLoadedAsync(_http, region);
            await MusicMaster.EnsureLoadedAsync(_http, region);

            if (!MysekaiMusicRecordMaster.HasData(region) || !MusicMaster.HasData(region))
            {
                SetStatus("未能加载所选区服的 master 数据（mysekaiMusicRecords/musics）。将使用回退映射，可能导致封面/曲目信息缺失。", true);
            }

            LoadCollectedMusicRecords(root);

            var maps = new Dictionary<int, MapData>();
            _musicRecords.Clear();

            foreach (var mp in mapsArray.EnumerateArray())
            {
                var siteId = mp.GetProperty("mysekaiSiteId").GetInt32();
                var mapDetails = new List<MapPoint>();

                if (mp.TryGetProperty("userMysekaiSiteHarvestFixtures", out var fixtures))
                {
                    foreach (var fx in fixtures.EnumerateArray())
                    {
                        var status = fx.GetProperty("userMysekaiSiteHarvestFixtureStatus").GetString();
                        if (string.Equals(status, "spawned", StringComparison.OrdinalIgnoreCase))
                        {
                            var px = fx.GetProperty("positionX").GetDouble();
                            var pz = fx.GetProperty("positionZ").GetDouble();
                            var fid = fx.GetProperty("mysekaiSiteHarvestFixtureId").GetInt32();
                            mapDetails.Add(new MapPoint
                            {
                                X = px,
                                Y = pz,
                                FixtureId = fid,
                                Rewards = new Dictionary<string, Dictionary<string, int>>()
                            });
                        }
                    }
                }

                if (mp.TryGetProperty("userMysekaiSiteHarvestResourceDrops", out var drops))
                {
                    foreach (var dr in drops.EnumerateArray())
                    {
                        var px = dr.GetProperty("positionX").GetDouble();
                        var pz = dr.GetProperty("positionZ").GetDouble();
                        var resourceType = dr.GetProperty("resourceType").GetString()!;
                        var resourceId = dr.GetProperty("resourceId").GetInt32();
                        var qty = dr.GetProperty("quantity").GetInt32();

                        var pt = mapDetails.FirstOrDefault(d => Math.Abs(d.X - px) < 1e-6 && Math.Abs(d.Y - pz) < 1e-6);
                        if (pt != null)
                        {
                            if (!pt.Rewards.TryGetValue(resourceType, out var bag))
                            {
                                bag = new Dictionary<string, int>();
                                pt.Rewards[resourceType] = bag;
                            }
                            bag[resourceId.ToString()] = bag.TryGetValue(resourceId.ToString(), out var old) ? old + qty : qty;

                            if (resourceType == "mysekai_music_record")
                            {
                                var entry = new MusicRecordEntry();
                                int viewId = ResolveMusicViewId(region, resourceId);
                                if (MusicMaster.TryGet(region, viewId, out var mm))
                                {
                                    var baseInfo = string.IsNullOrWhiteSpace(mm.Creator) ? mm.Title : $"{mm.Title} · {mm.Creator}";
                                    entry.Info = AppendCollectStatus(baseInfo, resourceId);
                                    entry.JacketUrl = MusicMaster.BuildJacketUrl(mm.AssetbundleName);
                                    entry.MetaUrl = $"https://sekai.best/music/{viewId}";
                                }
                                else
                                {
                                    entry.Info = AppendCollectStatus($"ID: {resourceId} (ViewID: {viewId})", resourceId);
                                    entry.MetaUrl = $"https://sekai.best/music/{viewId}";
                                    entry.JacketUrl = $"https://storage.sekai.best/sekai-cn-assets/music/jacket/jacket_s_{viewId:D3}/jacket_s_{viewId:D3}.webp";
                                }
                                _ = entry.InitializeJacketImageAsync(_http);
                                _musicRecords.Add(entry);
                            }
                        }
                    }
                }

                maps[siteId] = new MapData
                {
                    Name = _scenes.TryGetValue(siteId, out var sc) ? sc.Name : $"未知地图({siteId})",
                    Points = mapDetails
                };
            }

            _maps = maps;

            MapSelectCombo.Items.Clear();
            foreach (var kv in _maps)
            {
                MapSelectCombo.Items.Add(new ComboBoxItem { Content = $"{kv.Key}: {kv.Value.Name}", Tag = kv.Key });
            }
            MapSelectCombo.SelectedIndex = _maps.Count > 0 ? 0 : -1;

            if (_maps.Count > 0)
            {
                _currentSiteId = _maps.Keys.First();
                await DrawCurrentMapAsync();
                SetStatus($"已加载 JSON: {IOPath.GetFileName(path)}");
            }
            else
            {
                SetStatus("JSON 中没有可用地图。", true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private async Task DrawCurrentMapAsync()
    {
        if (!_maps.TryGetValue(_currentSiteId, out var map) || !_scenes.TryGetValue(_currentSiteId, out var scene))
        {
            return;
        }

        var img = MapImage;
        var canvas = MapCanvas;

        var bgPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", "sekai_xray", scene.ImagePath);
        if (!File.Exists(bgPath))
        {
            SetStatus($"找不到背景图: {bgPath}", true);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            img.Source = new Bitmap(bgPath);
            if (img.Source is Bitmap bmp)
            {
                img.Width = bmp.PixelSize.Width;
                img.Height = bmp.PixelSize.Height;
                canvas.Width = bmp.PixelSize.Width;
                canvas.Height = bmp.PixelSize.Height;
            }
            ApplyZoom(_zoom);
        });

        await RenderOverlayAsync(scene, map, canvas);
    }

    private static bool ContainsRare(MapPoint p, bool super = false)
    {
        var rareMaterial = new HashSet<int> { 5, 12, 20, 24, 32, 33, 61, 62, 63, 64, 65 };
        var superMaterial = new HashSet<int> { 5, 12, 20, 24 };
        var rareItem = new HashSet<int> { 7 };
        var rareFixture = new HashSet<int> { 118, 119, 120, 121 };

        foreach (var (rtype, items) in p.Rewards)
        {
            foreach (var kv in items)
            {
                if (!int.TryParse(kv.Key, out var id)) continue;
                if (rtype == "mysekai_material")
                {
                    if (super && superMaterial.Contains(id)) return true;
                    if (!super && rareMaterial.Contains(id)) return true;
                }
                else if (rtype == "mysekai_item")
                {
                    if (!super && rareItem.Contains(id)) return true;
                }
                else if (rtype == "mysekai_fixture")
                {
                    if (!super && rareFixture.Contains(id)) return true;
                }
            }
        }
        return false;
    }

    private async Task RenderOverlayAsync(MapScene scene, MapData map, Canvas canvas)
    {
        await Dispatcher.UIThread.InvokeAsync(() => canvas.Children.Clear());

        double originX = canvas.Width / 2.0 + scene.OffsetX;
        double originY = canvas.Height / 2.0 + scene.OffsetY;
        double grid = scene.PhysicalWidth;
        bool showIcons = ShowIconsCheckBox.IsChecked == true;

        var region = GetSelectedRegion();
        await MysekaiMusicRecordMaster.EnsureLoadedAsync(_http, region);
        await MusicMaster.EnsureLoadedAsync(_http, region);

        foreach (var p in map.Points)
        {
            double x = p.X, y = p.Y;
            if (scene.ReverseXY) (x, y) = (y, x);

            double displayX = scene.XDirection == XDir.Plus ? (originX + x * grid) : (originX - x * grid);
            double displayY = scene.YDirection == YDir.Plus ? (originY + y * grid) : (originY - y * grid);

            bool superRare = ContainsRare(p, true);
            bool rare = superRare || ContainsRare(p, false);

            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.Black,
                Stroke = rare ? (superRare ? Brushes.Red : Brushes.Blue) : Brushes.Gray,
                StrokeThickness = rare ? (superRare ? 3 : 2) : 1
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Canvas.SetLeft(dot, displayX - 5);
                Canvas.SetTop(dot, displayY - 5);
                canvas.Children.Add(dot);
            });

            if (!showIcons || p.Rewards.Count == 0) continue;

            int idx = 0;
            foreach (var kv in p.Rewards)
            {
                foreach (var item in kv.Value)
                {
                    if (string.Equals(kv.Key, "mysekai_music_record", StringComparison.Ordinal))
                    {
                        var imgIcon = new Image { Width = 24, Height = 24 };
                        if (int.TryParse(item.Key, out var rid))
                        {
                            string? jacketUrl = null;
                            int viewId = ResolveMusicViewId(region, rid);
                            if (MusicMaster.TryGet(region, viewId, out var mm))
                            {
                                jacketUrl = MusicMaster.BuildJacketUrl(mm.AssetbundleName);
                            }
                            else
                            {
                                jacketUrl = $"https://storage.sekai.best/sekai-cn-assets/music/jacket/jacket_s_{viewId:D3}/jacket_s_{viewId:D3}.webp";
                            }

                            var bitmap = await TryLoadBitmapFromUrl(jacketUrl);
                            if (bitmap != null)
                            {
                                imgIcon.Source = bitmap;
                            }
                        }

                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            Canvas.SetLeft(imgIcon, displayX + idx * 28);
                            Canvas.SetTop(imgIcon, displayY - 12);
                            canvas.Children.Add(imgIcon);
                        });

                        if (rare)
                        {
                            var rect = new Rectangle { Width = 30, Height = 30, StrokeThickness = 2 };
                            rect.Stroke = superRare ? Brushes.Red : Brushes.Blue;
                            rect.Fill = Brushes.Transparent;
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                Canvas.SetLeft(rect, displayX + idx * 28 - 3);
                                Canvas.SetTop(rect, displayY - 15);
                                canvas.Children.Add(rect);
                            });
                        }

                        idx++;
                        continue;
                    }

                    var iconRel = IconResolver.GetIconRelativePath(kv.Key, item.Key);
                    if (iconRel == null) continue;
                    var iconPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", "sekai_xray", iconRel);
                    if (!File.Exists(iconPath)) continue;

                    var bmpIcon = new Bitmap(iconPath);
                    var imgIconDefault = new Image { Source = bmpIcon, Width = 24, Height = 24 };
                    var iconContainer = new Grid { Width = 24, Height = 24 };
                    iconContainer.Children.Add(imgIconDefault);

                    if (string.Equals(kv.Key, "mysekai_material", StringComparison.Ordinal) && item.Value > 1)
                    {
                        var badge = new Border
                        {
                            Background = Brushes.Black,
                            CornerRadius = new CornerRadius(2),
                            Padding = new Thickness(2, 0, 2, 0),
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Opacity = 0.7
                        };
                        badge.Child = new TextBlock
                        {
                            Text = item.Value.ToString(),
                            Foreground = Brushes.White,
                            FontSize = 10
                        };
                        iconContainer.Children.Add(badge);
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Canvas.SetLeft(iconContainer, displayX + idx * 28);
                        Canvas.SetTop(iconContainer, displayY - 12);
                        canvas.Children.Add(iconContainer);
                    });

                    if (rare)
                    {
                        var rect = new Rectangle { Width = 30, Height = 30, StrokeThickness = 2 };
                        rect.Stroke = superRare ? Brushes.Red : Brushes.Blue;
                        rect.Fill = Brushes.Transparent;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            Canvas.SetLeft(rect, displayX + idx * 28 - 3);
                            Canvas.SetTop(rect, displayY - 15);
                            canvas.Children.Add(rect);
                        });
                    }

                    idx++;
                }
            }
        }
    }

    private static int ResolveMusicViewId(string region, int resourceId)
    {
        if (MysekaiMusicRecordMaster.TryGetExternalId(region, resourceId, out var externalId))
        {
            return externalId;
        }

        Debug.WriteLine($"[MusicRecord] Missing externalId for record {resourceId}, fallback to +30 offset.");
        return resourceId + 30;
    }

    private string GetSelectedRegion()
    {
        return (RegionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "jp";
    }

    private void LoadCollectedMusicRecords(JsonElement root)
    {
        _collectedMusicRecordIds.Clear();
        _hasCollectedMusicRecordData = false;

        JsonElement lookupRoot = root;
        if (!root.TryGetProperty("userMysekaiMusicRecords", out var collected) && root.TryGetProperty("updatedResources", out var updated))
        {
            lookupRoot = updated;
        }

        if (lookupRoot.TryGetProperty("userMysekaiMusicRecords", out collected) && collected.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in collected.EnumerateArray())
            {
                if (item.TryGetProperty("mysekaiMusicRecordId", out var idEl))
                {
                    _collectedMusicRecordIds.Add(idEl.GetInt32());
                }
            }
            _hasCollectedMusicRecordData = _collectedMusicRecordIds.Count > 0;
        }
    }

    private string AppendCollectStatus(string baseInfo, int resourceId)
    {
        if (!_hasCollectedMusicRecordData) return baseInfo;
        var suffix = _collectedMusicRecordIds.Contains(resourceId) ? "已收集" : "未收集";
        return $"{baseInfo} · {suffix}";
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.25, 4.0);
        MapZoomContainer.RenderTransform = new ScaleTransform(_zoom, _zoom);
    }

    private void SetStatus(string message, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = message;
            StatusText.Foreground = isError ? Brushes.IndianRed : Brushes.Black;
        });
    }

    private static async Task<Bitmap?> TryLoadBitmapFromUrl(string url)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            await using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}

internal class MusicRecordEntry : System.ComponentModel.INotifyPropertyChanged
{
    public string Info { get; set; } = string.Empty;
    public string MetaUrl { get; set; } = string.Empty;
    public string JacketUrl { get; set; } = string.Empty;

    private Bitmap? _jacketImage;
    public Bitmap? JacketImage
    {
        get => _jacketImage;
        set
        {
            if (_jacketImage != value)
            {
                _jacketImage = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(JacketImage)));
            }
        }
    }

    private string _loadStatus = "Loading...";
    public string LoadStatus
    {
        get => _loadStatus;
        set
        {
            if (_loadStatus != value)
            {
                _loadStatus = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(LoadStatus)));
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeJacketImageAsync(HttpClient httpClient)
    {
        if (string.IsNullOrEmpty(JacketUrl))
        {
            LoadStatus = "No URL";
            return;
        }

        try
        {
            LoadStatus = "Downloading...";
            var bytes = await httpClient.GetByteArrayAsync(JacketUrl);
            await using var ms = new MemoryStream(bytes);
            JacketImage = new Bitmap(ms);
            LoadStatus = "OK";
        }
        catch (Exception ex)
        {
            LoadStatus = $"Failed: {ex.Message}";
        }
    }
}

internal class MapData
{
    public string Name { get; set; } = string.Empty;
    public List<MapPoint> Points { get; set; } = new();
}

internal class MapPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public int FixtureId { get; set; }
    public Dictionary<string, Dictionary<string, int>> Rewards { get; set; } = new();
}

internal enum XDir { Plus, Minus }
internal enum YDir { Plus, Minus }

internal class MapScene
{
    public string Name { get; init; } = string.Empty;
    public double PhysicalWidth { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public string ImagePath { get; init; } = string.Empty;
    public XDir XDirection { get; init; }
    public YDir YDirection { get; init; }
    public bool ReverseXY { get; init; }

    public static Dictionary<int, MapScene> CreateDefaults() => new()
    {
        [1] = new MapScene { Name = "マイホーム", PhysicalWidth = 33.333, OffsetX = 0, OffsetY = -40, ImagePath = IOPath.Combine("img", "grassland.png"), XDirection = XDir.Minus, YDirection = YDir.Minus, ReverseXY = true },
        [2] = new MapScene { Name = "1F", PhysicalWidth = 24.806, OffsetX = -62.015, OffsetY = 20.672, ImagePath = IOPath.Combine("img", "flowergarden.png"), XDirection = XDir.Minus, YDirection = YDir.Minus, ReverseXY = true },
        [3] = new MapScene { Name = "2F", PhysicalWidth = 20.513, OffsetX = 0, OffsetY = 80, ImagePath = IOPath.Combine("img", "beach.png"), XDirection = XDir.Plus, YDirection = YDir.Minus, ReverseXY = false },
        [4] = new MapScene { Name = "3F", PhysicalWidth = 21.333, OffsetX = 0, OffsetY = -106.667, ImagePath = IOPath.Combine("img", "memorialplace.png"), XDirection = XDir.Plus, YDirection = YDir.Minus, ReverseXY = false },
        [5] = new MapScene { Name = "さいしょの原っぱ", PhysicalWidth = 33.333, OffsetX = 0, OffsetY = -40, ImagePath = IOPath.Combine("img", "grassland.png"), XDirection = XDir.Minus, YDirection = YDir.Minus, ReverseXY = true },
        [6] = new MapScene { Name = "願いの砂浜", PhysicalWidth = 20.513, OffsetX = 0, OffsetY = 80, ImagePath = IOPath.Combine("img", "beach.png"), XDirection = XDir.Plus, YDirection = YDir.Minus, ReverseXY = false },
        [7] = new MapScene { Name = "彩りの花畑", PhysicalWidth = 24.806, OffsetX = -62.015, OffsetY = 20.672, ImagePath = IOPath.Combine("img", "flowergarden.png"), XDirection = XDir.Minus, YDirection = YDir.Minus, ReverseXY = true },
        [8] = new MapScene { Name = "忘れ去られた場所", PhysicalWidth = 21.333, OffsetX = 0, OffsetY = -106.667, ImagePath = IOPath.Combine("img", "memorialplace.png"), XDirection = XDir.Plus, YDirection = YDir.Minus, ReverseXY = false },
    };
}

internal static class IconResolver
{
    public static string? GetIconRelativePath(string resourceType, string itemId)
    {
        return resourceType switch
        {
            "mysekai_item" when itemId == "7" => IOPath.Combine("icon", "Texture2D", "item_blueprint_fragment.png"),
            "mysekai_material" => itemId switch
            {
                "1" => IOPath.Combine("icon", "Texture2D", "item_wood_1.png"),
                "2" => IOPath.Combine("icon", "Texture2D", "item_wood_2.png"),
                "3" => IOPath.Combine("icon", "Texture2D", "item_wood_3.png"),
                "4" => IOPath.Combine("icon", "Texture2D", "item_wood_4.png"),
                "5" => IOPath.Combine("icon", "Texture2D", "item_wood_5.png"),
                "6" => IOPath.Combine("icon", "Texture2D", "item_mineral_1.png"),
                "7" => IOPath.Combine("icon", "Texture2D", "item_mineral_2.png"),
                "8" => IOPath.Combine("icon", "Texture2D", "item_mineral_3.png"),
                "9" => IOPath.Combine("icon", "Texture2D", "item_mineral_4.png"),
                "10" => IOPath.Combine("icon", "Texture2D", "item_mineral_5.png"),
                "11" => IOPath.Combine("icon", "Texture2D", "item_mineral_6.png"),
                "12" => IOPath.Combine("icon", "Texture2D", "item_mineral_7.png"),
                "13" => IOPath.Combine("icon", "Texture2D", "item_junk_1.png"),
                "14" => IOPath.Combine("icon", "Texture2D", "item_junk_2.png"),
                "15" => IOPath.Combine("icon", "Texture2D", "item_junk_3.png"),
                "16" => IOPath.Combine("icon", "Texture2D", "item_junk_4.png"),
                "17" => IOPath.Combine("icon", "Texture2D", "item_junk_5.png"),
                "18" => IOPath.Combine("icon", "Texture2D", "item_junk_6.png"),
                "19" => IOPath.Combine("icon", "Texture2D", "item_junk_7.png"),
                "20" => IOPath.Combine("icon", "Texture2D", "item_plant_1.png"),
                "21" => IOPath.Combine("icon", "Texture2D", "item_plant_2.png"),
                "22" => IOPath.Combine("icon", "Texture2D", "item_plant_3.png"),
                "23" => IOPath.Combine("icon", "Texture2D", "item_plant_4.png"),
                "24" => IOPath.Combine("icon", "Texture2D", "item_tone_8.png"),
                "32" => IOPath.Combine("icon", "Texture2D", "item_junk_8.png"),
                "33" => IOPath.Combine("icon", "Texture2D", "item_mineral_8.png"),
                "34" => IOPath.Combine("icon", "Texture2D", "item_junk_9.png"),
                "61" => IOPath.Combine("icon", "Texture2D", "item_junk_10.png"),
                "62" => IOPath.Combine("icon", "Texture2D", "item_junk_11.png"),
                "63" => IOPath.Combine("icon", "Texture2D", "item_junk_12.png"),
                "64" => IOPath.Combine("icon", "Texture2D", "item_mineral_9.png"),
                "65" => IOPath.Combine("icon", "Texture2D", "item_mineral_10.png"),
                _ => null
            },
            "mysekai_fixture" => itemId switch
            {
                "118" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sapling1_118.png"),
                "119" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sapling1_119.png"),
                "120" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sapling1_120.png"),
                "121" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sapling1_121.png"),
                "126" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_126.png"),
                "127" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_127.png"),
                "128" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_128.png"),
                "129" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_129.png"),
                "130" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_130.png"),
                "474" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_474.png"),
                "475" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_475.png"),
                "476" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_476.png"),
                "477" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_477.png"),
                "478" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_478.png"),
                "479" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_479.png"),
                "480" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_480.png"),
                "481" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_481.png"),
                "482" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_482.png"),
                "483" => IOPath.Combine("icon", "Texture2D", "mdl_non1001_before_sprout1_483.png"),
                _ => null
            },
            "mysekai_music_record" => IOPath.Combine("icon", "Texture2D", "item_surplus_music_record.png"),
            _ => null
        };
    }
}

internal static class MysekaiMusicRecordMaster
{
    private static readonly string LocalFallbackPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", "mysekaiMusicRecords.json");
    private static readonly Dictionary<string, Dictionary<int, int>> _mapByRegion = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task> _loadingByRegion = new(StringComparer.OrdinalIgnoreCase);

    public static async Task EnsureLoadedAsync(HttpClient http, string region)
    {
        var key = MasterDbEndpoints.NormalizeRegion(region);
        if (_mapByRegion.TryGetValue(key, out var map) && map.Count > 0) return;
        if (_loadingByRegion.TryGetValue(key, out var loading))
        {
            await loading;
            return;
        }

        var task = LoadAsync(http, key);
        _loadingByRegion[key] = task;
        try { await task; }
        finally { _loadingByRegion.Remove(key); }
    }

    private static async Task LoadAsync(HttpClient http, string region)
    {
        if (await TryLoadFromRemoteAsync(http, region)) return;
        if (await TryLoadFromLocalAsync(region)) return;
    }

    private static async Task<bool> TryLoadFromRemoteAsync(HttpClient http, string region)
    {
        foreach (var url in MasterDbEndpoints.GetMysekaiMusicRecordsUrls(region))
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                if (TryPopulateMap(bytes, region)) return true;
            }
            catch
            {
                // ignore and try next
            }
        }

        return false;
    }

    private static async Task<bool> TryLoadFromLocalAsync(string region)
    {
        try
        {
            if (!File.Exists(LocalFallbackPath)) return false;
            var bytes = await File.ReadAllBytesAsync(LocalFallbackPath);
            return TryPopulateMap(bytes, region);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPopulateMap(byte[] bytes, string region)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return TryPopulateMap(doc.RootElement, region);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPopulateMap(JsonElement root, string region)
    {
        if (root.ValueKind != JsonValueKind.Array) return false;

        var map = new Dictionary<int, int>();
        foreach (var el in root.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var idEl) || !el.TryGetProperty("externalId", out var extEl)) continue;

            var trackType = el.TryGetProperty("mysekaiMusicTrackType", out var typeEl) ? typeEl.GetString() : null;
            if (!string.Equals(trackType, "music", StringComparison.OrdinalIgnoreCase)) continue;

            map[idEl.GetInt32()] = extEl.GetInt32();
        }

        if (map.Count == 0) return false;
        _mapByRegion[region] = map;
        return true;
    }

    public static bool TryGetExternalId(string region, int id, out int externalId)
    {
        var key = MasterDbEndpoints.NormalizeRegion(region);
        if (_mapByRegion.TryGetValue(key, out var map))
        {
            return map.TryGetValue(id, out externalId);
        }

        externalId = default;
        return false;
    }

    public static bool HasData(string region)
    {
        var key = MasterDbEndpoints.NormalizeRegion(region);
        return _mapByRegion.TryGetValue(key, out var map) && map.Count > 0;
    }
}

internal static class MusicMaster
{
    private static readonly string LocalFallbackPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", "musics.json");
    private static readonly Dictionary<string, Dictionary<int, MusicMasterEntry>> _mapByRegion = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task> _loadingByRegion = new(StringComparer.OrdinalIgnoreCase);

    public static async Task EnsureLoadedAsync(HttpClient http, string region)
    {
        var key = MasterDbEndpoints.NormalizeRegion(region);
        if (_mapByRegion.TryGetValue(key, out var map) && map.Count > 0) return;
        if (_loadingByRegion.TryGetValue(key, out var loading))
        {
            await loading;
            return;
        }

        var task = LoadAsync(http, key);
        _loadingByRegion[key] = task;
        try { await task; }
        finally { _loadingByRegion.Remove(key); }
    }

    private static async Task LoadAsync(HttpClient http, string region)
    {
        if (await TryLoadFromRemoteAsync(http, region)) return;
        if (await TryLoadFromLocalAsync(region)) return;
    }

    private static async Task<bool> TryLoadFromRemoteAsync(HttpClient http, string region)
    {
        foreach (var url in MasterDbEndpoints.GetMusicsUrls(region))
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                if (TryPopulateMap(bytes, region)) return true;
            }
            catch
            {
                // ignore and try next
            }
        }

        return false;
    }

    private static async Task<bool> TryLoadFromLocalAsync(string region)
    {
        try
        {
            if (!File.Exists(LocalFallbackPath)) return false;
            var bytes = await File.ReadAllBytesAsync(LocalFallbackPath);
            return TryPopulateMap(bytes, region);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPopulateMap(byte[] bytes, string region)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return TryPopulateMap(doc.RootElement, region);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPopulateMap(JsonElement root, string region)
    {
        if (root.ValueKind != JsonValueKind.Array) return false;

        var map = new Dictionary<int, MusicMasterEntry>();
        foreach (var el in root.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var idEl)) continue;
            int id = idEl.GetInt32();
            string asset = el.TryGetProperty("assetbundleName", out var ab) ? ab.GetString() ?? string.Empty : string.Empty;

            string title = el.TryGetProperty("title", out var t0) ? (t0.GetString() ?? string.Empty) : string.Empty;
            string creator = string.Empty;
            if (el.TryGetProperty("infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
            {
                var first = infos.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    title = first.TryGetProperty("title", out var ti) ? (ti.GetString() ?? title) : title;
                    creator = first.TryGetProperty("creator", out var cr) ? (cr.GetString() ?? string.Empty) : string.Empty;
                }
            }

            if (!string.IsNullOrWhiteSpace(asset))
            {
                map[id] = new MusicMasterEntry { Id = id, AssetbundleName = asset, Title = title, Creator = creator };
            }
        }

        if (map.Count == 0) return false;
        _mapByRegion[region] = map;
        return true;
    }

    public static bool TryGet(string region, int id, out MusicMasterEntry entry)
    {
        var key = MasterDbEndpoints.NormalizeRegion(region);
        if (_mapByRegion.TryGetValue(key, out var map))
        {
            return map.TryGetValue(id, out entry!);
        }

        entry = null!;
        return false;
    }

    public static bool HasData(string region)
    {
        var key = MasterDbEndpoints.NormalizeRegion(region);
        return _mapByRegion.TryGetValue(key, out var map) && map.Count > 0;
    }

    public static string BuildJacketUrl(string assetbundleName)
    {
        return $"https://storage.sekai.best/sekai-cn-assets/music/jacket/{assetbundleName}/{assetbundleName}.webp";
    }
}

internal class MusicMasterEntry
{
    public int Id { get; set; }
    public string AssetbundleName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
}

internal static class MasterDbEndpoints
{
    public static string NormalizeRegion(string region)
    {
        if (string.IsNullOrWhiteSpace(region)) return "jp";
        return region.Trim().ToLowerInvariant() switch
        {
            "jp" => "jp",
            "tw" => "tw",
            "en" => "en",
            "kr" => "kr",
            "cn" => "cn",
            _ => "jp"
        };
    }

    public static IEnumerable<string> GetMysekaiMusicRecordsUrls(string region)
    {
        foreach (var baseUrl in GetBaseUrls(region))
        {
            yield return $"{baseUrl}/mysekaiMusicRecords.json";
        }
    }

    public static IEnumerable<string> GetMusicsUrls(string region)
    {
        foreach (var baseUrl in GetBaseUrls(region))
        {
            yield return $"{baseUrl}/musics.json";
        }
    }

    private static IEnumerable<string> GetBaseUrls(string region)
    {
        var key = NormalizeRegion(region);
        yield return key switch
        {
            "jp" => "https://sekai-world.github.io/sekai-master-db-diff",
            "tw" => "https://sekai-world.github.io/sekai-master-db-tc-diff",
            "en" => "https://sekai-world.github.io/sekai-master-db-en-diff",
            "kr" => "https://sekai-world.github.io/sekai-master-db-kr-diff",
            "cn" => "https://sekai-world.github.io/sekai-master-db-cn-diff",
            _ => "https://sekai-world.github.io/sekai-master-db-diff"
        };

        yield return key switch
        {
            "jp" => "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-diff/main",
            "tw" => "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-tc-diff/main",
            "en" => "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-en-diff/main",
            "kr" => "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-kr-diff/main",
            "cn" => "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-cn-diff/main",
            _ => "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-diff/main"
        };
    }
}
