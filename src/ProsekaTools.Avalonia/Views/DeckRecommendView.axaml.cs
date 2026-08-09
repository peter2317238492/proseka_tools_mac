using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ProsekaTools.Avalonia.Services;
using IOPath = System.IO.Path;

namespace ProsekaTools.Avalonia.Views;

public sealed partial class DeckRecommendView : UserControl
{
    private const int MaxStatusLines = 200;
    private const string MusicMetasDownloadUrl = "https://storage.sekai.best/sekai-best-assets/music_metas.json";
    private const string EventsUrlJp = "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-diff/main/events.json";
    private const string EventsUrlTw = "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-tc-diff/main/events.json";
    private const string EventsUrlEn = "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-en-diff/main/events.json";
    private const string EventsUrlKr = "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-kr-diff/main/events.json";
    private const string EventsUrlCn = "https://raw.githubusercontent.com/Sekai-World/sekai-master-db-cn-diff/main/events.json";

    private static readonly string DefaultMasterDataDir = IOPath.Combine(AppContext.BaseDirectory, "Assets", "master");
    private static readonly string MusicMetasFallbackPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", "music_metas.json");
    private static readonly string MusicMetasDownloadPath = IOPath.Combine(AppPaths.AppDataRoot, "cache", "music_metas.json");
    private static readonly string EventsFallbackPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", "master", "events.json");
    private static readonly string EventsCacheDir = IOPath.Combine(AppPaths.AppDataRoot, "cache", "events");

    private readonly ObservableCollection<DeckRecommendEntry> _results = new();
    private readonly OwnedCardCacheService _cacheService = new();
    private readonly SemaphoreSlim _cardDatabaseGate = new(1, 1);
    private List<DeckRecommendSekaiCard>? _cardDatabase;
    private readonly SemaphoreSlim _musicNameGate = new(1, 1);
    private Dictionary<int, string>? _musicNameMap;
    private readonly HttpClient _http;
    private string? _suiteJsonPath;
    private string? _masterDataDir;
    private string? _musicMetasPath;
    private string? _pythonExePath;
    private string? _lastResultPath;
    private bool _defaultsReady;

    public DeckRecommendView()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7");
        _http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        _http.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");

        RegionCombo.SelectionChanged += async (_, _) => await RegionCombo_SelectionChangedAsync();
        ChooseSuiteJsonButton.Click += async (_, _) => await ChooseSuiteJsonAsync();
        ChooseMasterDataButton.Click += async (_, _) => await ChooseMasterDataAsync();
        ChooseMusicMetasButton.Click += async (_, _) => await ChooseMusicMetasAsync();
        ChoosePythonButton.Click += async (_, _) => await ChoosePythonAsync();
        RunButton.Click += async (_, _) => await RunButtonAsync();
        DecryptAndRunButton.Click += async (_, _) => await DecryptAndRunButtonAsync();
        ClearResultsButton.Click += (_, _) => ClearResults();

        AttachedToVisualTree += async (_, _) => await InitializeDefaultsAsync();
    }

    private async Task InitializeDefaultsAsync()
    {
        if (_defaultsReady) return;

        if (Directory.Exists(DefaultMasterDataDir))
        {
            _masterDataDir = DefaultMasterDataDir;
            MasterDataTextBox.Text = _masterDataDir;
        }

        var musicMetasPath = await ResolveMusicMetasDefaultAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(musicMetasPath))
        {
            _musicMetasPath = musicMetasPath;
            MusicMetasTextBox.Text = _musicMetasPath;
        }

        var python = FindBundledPython();
        if (!string.IsNullOrWhiteSpace(python))
        {
            _pythonExePath = python;
            PythonExeTextBox.Text = _pythonExePath;
        }

        await UpdateDefaultEventIdAsync().ConfigureAwait(true);
        _defaultsReady = true;
    }

    private async Task RegionCombo_SelectionChangedAsync()
    {
        if (!_defaultsReady) return;
        await UpdateDefaultEventIdAsync().ConfigureAwait(true);
    }

    private async Task UpdateDefaultEventIdAsync()
    {
        var region = (RegionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "cn";
        var eventId = await GetCurrentEventIdAsync(region).ConfigureAwait(true);
        EventIdTextBox.Text = eventId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<int?> GetCurrentEventIdAsync(string region)
    {
        var bytes = await TryLoadEventsJsonAsync(region).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int? currentId = null;
            long currentStart = long.MinValue;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!TryGetLong(el, out var startAt, "startAt")) continue;
                if (!TryGetLong(el, out var aggregateAt, "aggregateAt")) continue;
                if (startAt <= now && now < aggregateAt)
                {
                    if (TryGetInt(el, out var id, "id") && startAt >= currentStart)
                    {
                        currentStart = startAt;
                        currentId = id;
                    }
                }
            }

            return currentId;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeckRecommend] events.json parse failed: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> TryLoadEventsJsonAsync(string region)
    {
        try
        {
            var url = GetEventsUrl(region);
            var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes.Length > 0)
            {
                var cachePath = GetEventsCachePath(region);
                var dir = IOPath.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    AppPaths.EnsureDir(dir);
                }
                await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
                return bytes;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeckRecommend] events.json download failed: {ex.Message}");
        }

        try
        {
            if (File.Exists(EventsFallbackPath))
            {
                return await File.ReadAllBytesAsync(EventsFallbackPath).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeckRecommend] events.json fallback failed: {ex.Message}");
        }

        return null;
    }

    private static string GetEventsUrl(string region)
    {
        return region switch
        {
            "jp" => EventsUrlJp,
            "tw" => EventsUrlTw,
            "en" => EventsUrlEn,
            "kr" => EventsUrlKr,
            "cn" => EventsUrlCn,
            _ => EventsUrlJp
        };
    }

    private static string GetEventsCachePath(string region)
    {
        return IOPath.Combine(EventsCacheDir, $"events_{region}.json");
    }

    private static bool TryGetLong(JsonElement el, out long value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!el.TryGetProperty(key, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
            {
                return true;
            }
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out value))
            {
                return true;
            }
        }
        value = 0;
        return false;
    }

    private async Task<string?> ResolveMusicMetasDefaultAsync()
    {
        if (await TryEnsureMusicMetasDownloadedAsync().ConfigureAwait(true))
        {
            if (File.Exists(MusicMetasDownloadPath))
            {
                return MusicMetasDownloadPath;
            }
        }

        if (File.Exists(MusicMetasFallbackPath))
        {
            return MusicMetasFallbackPath;
        }

        return null;
    }

    private async Task<bool> TryEnsureMusicMetasDownloadedAsync()
    {
        try
        {
            if (File.Exists(MusicMetasDownloadPath))
            {
                return true;
            }

            var bytes = await _http.GetByteArrayAsync(MusicMetasDownloadUrl).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                return false;
            }

            var dir = IOPath.GetDirectoryName(MusicMetasDownloadPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                AppPaths.EnsureDir(dir);
            }
            await File.WriteAllBytesAsync(MusicMetasDownloadPath, bytes).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeckRecommend] Music metas download failed: {ex.Message}");
            return false;
        }
    }

    private async Task ChooseSuiteJsonAsync()
    {
        if (UseLatestSuiteJsonCheckBox.IsChecked == true)
        {
            var latest = TryGetLatestSuiteJson();
            if (latest == null)
            {
                SetStatus("未在 output/owned_cards 找到 JSON。", isError: true);
                return;
            }
            _suiteJsonPath = latest;
            SuiteJsonTextBox.Text = _suiteJsonPath;
            SetStatus($"已选择最新: {IOPath.GetFileName(_suiteJsonPath)}");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            SetStatus("无法打开文件选择器。", isError: true);
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
            }
        });

        var file = files.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _suiteJsonPath = path;
            SuiteJsonTextBox.Text = _suiteJsonPath;
            SetStatus($"已选择: {IOPath.GetFileName(_suiteJsonPath)}");
        }
    }

    private async Task ChooseMasterDataAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            SetStatus("无法打开目录选择器。", isError: true);
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        var path = folder?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _masterDataDir = path;
            MasterDataTextBox.Text = _masterDataDir;
            SetStatus($"已选择: {IOPath.GetFileName(_masterDataDir)}");
        }
    }

    private async Task ChooseMusicMetasAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            SetStatus("无法打开文件选择器。", isError: true);
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
            }
        });

        var file = files.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _musicMetasPath = path;
            MusicMetasTextBox.Text = _musicMetasPath;
            SetStatus($"已选择: {IOPath.GetFileName(_musicMetasPath)}");
        }
    }

    private async Task ChoosePythonAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            SetStatus("无法打开文件选择器。", isError: true);
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Python") { Patterns = new[] { "python", "python3", "*.exe" } }
            }
        });

        var file = files.FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _pythonExePath = path;
            PythonExeTextBox.Text = _pythonExePath;
            SetStatus($"已选择: {IOPath.GetFileName(_pythonExePath)}");
        }
    }

    private void ClearResults()
    {
        _results.Clear();
        ResultFileText.Text = string.Empty;
    }

    private async Task RunButtonAsync()
    {
        try
        {
            WorkingBar.IsVisible = true;
            await RunDeckRecommendCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            WorkingBar.IsVisible = false;
        }
    }

    private async Task DecryptAndRunButtonAsync()
    {
        try
        {
            WorkingBar.IsVisible = true;
            StatusText.Text = string.Empty;

            var inputFile = TryGetLatestSuiteCapture();
            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                SetStatus("未在 captures/suite 找到文件。", isError: true);
                return;
            }

            var region = (RegionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "jp";
            var outputDir = AppPaths.OutputOwnedCardsDir;
            Directory.CreateDirectory(outputDir);
            var outName = IOPath.GetFileNameWithoutExtension(inputFile) + ".json";
            var outputPath = IOPath.Combine(outputDir, outName);

            var (exePath, args) = BuildSssekaiCommand(inputFile, outputPath, region);
            if (exePath is null || args is null)
            {
                SetStatus("未找到内置 Python 或 sssekai 模块。", isError: true);
                return;
            }

            var ok = await RunProcessAsync(exePath, args).ConfigureAwait(true);
            if (ok && File.Exists(outputPath))
            {
                SetStatus($"解密完成: {outputPath}");
                _suiteJsonPath = outputPath;
                SuiteJsonTextBox.Text = _suiteJsonPath;
                await RunDeckRecommendCoreAsync(outputPath).ConfigureAwait(true);
            }
            else
            {
                SetStatus("解密失败，请检查输入文件和 region。", isError: true);
            }
        }
        finally
        {
            WorkingBar.IsVisible = false;
        }
    }

    private async Task RunDeckRecommendCoreAsync(string? suitePathOverride = null)
    {
        string? suitePath = suitePathOverride;
        if (string.IsNullOrWhiteSpace(suitePath))
        {
            suitePath = _suiteJsonPath;
            if (UseLatestSuiteJsonCheckBox.IsChecked == true || string.IsNullOrWhiteSpace(suitePath))
            {
                suitePath = TryGetLatestSuiteJson();
            }
        }
        if (string.IsNullOrWhiteSpace(suitePath) || !File.Exists(suitePath))
        {
            SetStatus("未选择有效的 suite JSON。", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_masterDataDir) || !Directory.Exists(_masterDataDir))
        {
            SetStatus("请选择有效的 masterdata 目录。", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(_musicMetasPath) || !File.Exists(_musicMetasPath))
        {
            SetStatus("请选择有效的 music metas JSON。", isError: true);
            return;
        }

        var region = (RegionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "jp";
        var liveType = (LiveTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "multi";
        var target = (TargetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "score";
        var algorithm = (AlgorithmCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ga";

        if (!TryParseInt(MusicIdTextBox.Text, out var musicId))
        {
            SetStatus("Music ID 无效。", isError: true);
            return;
        }
        var musicDiff = (MusicDiffCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "expert";

        int? eventId = null;
        if (!string.IsNullOrWhiteSpace(EventIdTextBox.Text))
        {
            if (!TryParseInt(EventIdTextBox.Text, out var parsedEventId))
            {
                SetStatus("Event ID 无效。", isError: true);
                return;
            }
            eventId = parsedEventId;
        }

        int limit = 10;
        if (!string.IsNullOrWhiteSpace(LimitTextBox.Text))
        {
            if (!TryParseInt(LimitTextBox.Text, out limit))
            {
                SetStatus("Limit 无效。", isError: true);
                return;
            }
        }

        var runnerPath = IOPath.Combine(AppContext.BaseDirectory, "Services", "Calc", "deck_recommend_runner.py");
        if (!File.Exists(runnerPath))
        {
            SetStatus($"找不到 runner 脚本: {runnerPath}", isError: true);
            return;
        }

        AppPaths.EnsureDir(AppPaths.OutputDeckRecommendDir);
        var outputName = $"deck_recommend_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        var outputPath = IOPath.Combine(AppPaths.OutputDeckRecommendDir, outputName);

        var pythonExe = ResolvePythonExe();
        var args = new List<string>
        {
            Quote(runnerPath),
            "--suite-json", Quote(suitePath),
            "--master-dir", Quote(_masterDataDir),
            "--music-metas", Quote(_musicMetasPath),
            "--region", region,
            "--live-type", liveType,
            "--music-id", musicId.ToString(CultureInfo.InvariantCulture),
            "--music-diff", musicDiff,
            "--target", target,
            "--algorithm", algorithm,
            "--limit", limit.ToString(CultureInfo.InvariantCulture),
            "--output", Quote(outputPath)
        };
        if (eventId.HasValue)
        {
            args.Add("--event-id");
            args.Add(eventId.Value.ToString(CultureInfo.InvariantCulture));
        }

        var argLine = string.Join(" ", args);
        var ok = await RunProcessAsync(pythonExe, argLine).ConfigureAwait(true);
        if (!ok)
        {
            SetStatus("组卡执行失败，请检查 runner 日志。", isError: true);
            return;
        }
        if (!File.Exists(outputPath))
        {
            SetStatus($"未生成结果文件: {outputPath}", isError: true);
            return;
        }
        _lastResultPath = outputPath;
        LoadResultFile(outputPath);
        var scoreSongs = GetScoreSongs(ScoreSongsTextBox.Text, musicDiff);
        await LoadDeckPreviewsAsync().ConfigureAwait(true);
        await ComputeMultiSongScoresAsync(scoreSongs, suitePath, _masterDataDir, _musicMetasPath, region, liveType, eventId).ConfigureAwait(true);
        SetStatus($"完成: {outputName}");
    }

    private string ResolvePythonExe()
    {
        if (!string.IsNullOrWhiteSpace(_pythonExePath)) return _pythonExePath;
        if (!string.IsNullOrWhiteSpace(PythonExeTextBox.Text)) return PythonExeTextBox.Text.Trim();
        return FindBundledPython() ?? "python3";
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
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = IOPath.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            };
            TryApplyBundledPythonEnv(psi);

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>();
            p.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _ = AppendStatusUIAsync(e.Data);
                }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _ = AppendStatusUIAsync(e.Data, isError: true);
                }
            };
            p.Exited += (_, _) => tcs.TrySetResult(p.ExitCode);

            if (!p.Start()) return false;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            var exit = await tcs.Task.ConfigureAwait(true);
            return exit == 0;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            return false;
        }
    }

    private static bool TryParseInt(string? text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private string? TryGetLatestSuiteJson()
    {
        try
        {
            var dir = AppPaths.OutputOwnedCardsDir;
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

    private string? TryGetLatestSuiteCapture()
    {
        try
        {
            var dir = AppPaths.CapturesSuiteDir;
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "*.bin")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private void LoadResultFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("decks", out var decks) || decks.ValueKind != JsonValueKind.Array)
            {
                SetStatus("结果缺少 decks。", isError: true);
                return;
            }

            _results.Clear();
            int index = 1;
            foreach (var deck in decks.EnumerateArray())
            {
                int score = GetInt(deck, "score");
                int liveScore = GetInt(deck, "live_score");
                int totalPower = GetInt(deck, "total_power");
                double eventBonus = GetDouble(deck, "event_bonus_rate");
                var cardPreviews = BuildCardPreviews(deck, out var effectiveSkill);

                _results.Add(new DeckRecommendEntry
                {
                    Title = $"#{index} score={score}",
                    TotalPower = totalPower,
                    EventBonusRate = eventBonus,
                    SummaryLine = $"effective={effectiveSkill:0.##} power={totalPower} event_bonus={eventBonus:0.##}%",
                    ScoreLine = $"live_score={liveScore} score={score}",
                    CardPreviews = new ObservableCollection<DeckCardPreview>(cardPreviews)
                });
                index++;
            }

            ResultFileText.Text = IOPath.GetFileName(path);
        }
        catch (Exception ex)
        {
            SetStatus($"解析失败: {ex.Message}", isError: true);
        }
    }

    private static List<DeckCardPreview> BuildCardPreviews(JsonElement deck, out double effectiveSkill)
    {
        effectiveSkill = 0;
        var previews = new List<DeckCardPreview>();
        if (!deck.TryGetProperty("cards", out var cardsEl) || cardsEl.ValueKind != JsonValueKind.Array)
        {
            return previews;
        }
        var cards = new List<(DeckCardPreview preview, int skill)>();
        foreach (var card in cardsEl.EnumerateArray())
        {
            if (card.TryGetProperty("card_id", out var idEl) && idEl.TryGetInt32(out var id))
            {
                bool afterTraining = card.TryGetProperty("after_training", out var atEl) && atEl.ValueKind == JsonValueKind.True;
                string defaultImage = card.TryGetProperty("default_image", out var diEl) && diEl.ValueKind == JsonValueKind.String
                    ? diEl.GetString() ?? string.Empty
                    : string.Empty;
                int skillScoreUp = 0;
                if (card.TryGetProperty("skill_score_up", out var skillEl))
                {
                    if (skillEl.ValueKind == JsonValueKind.Number && skillEl.TryGetInt32(out var v)) skillScoreUp = v;
                    else if (skillEl.ValueKind == JsonValueKind.String && int.TryParse(skillEl.GetString(), out v)) skillScoreUp = v;
                }
                var preview = new DeckCardPreview
                {
                    CardId = id,
                    AfterTraining = afterTraining,
                    DefaultImageType = defaultImage,
                    SkillScoreUp = skillScoreUp,
                    LoadStatus = "Pending"
                };
                cards.Add((preview, skillScoreUp));
            }
        }
        if (cards.Count == 0) return previews;

        int maxIndex = 0;
        int maxSkill = cards[0].skill;
        int sumSkill = cards[0].skill;
        for (int i = 1; i < cards.Count; i++)
        {
            int skill = cards[i].skill;
            sumSkill += skill;
            if (skill > maxSkill)
            {
                maxSkill = skill;
                maxIndex = i;
            }
        }
        if (cards.Count > 1)
        {
            effectiveSkill = maxSkill + (sumSkill - maxSkill) / 5.0;
        }
        else
        {
            effectiveSkill = maxSkill;
        }

        previews.Add(cards[maxIndex].preview);
        for (int i = 0; i < cards.Count; i++)
        {
            if (i == maxIndex) continue;
            previews.Add(cards[i].preview);
        }
        return previews;
    }

    private static int GetInt(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v)) return v;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out v)) return v;
        }
        return 0;
    }

    private static double GetDouble(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var v)) return v;
            if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
        }
        return 0;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private Task SetStatusUIAsync(string message, bool isError = false)
    {
        return EnqueueOnUIAsync(() => SetStatus(message, isError));
    }

    private Task AppendStatusUIAsync(string message, bool isError = false)
    {
        return EnqueueOnUIAsync(() => AppendStatus(message, isError));
    }

    private static List<ScoreSong> GetScoreSongs(string? input, string fallbackDiff)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new List<ScoreSong>
            {
                new(226, "hard"),
                new(74, "expert"),
                new(448, "expert")
            };
        }

        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return new List<ScoreSong> { new(id, fallbackDiff) };
            }
        }

        return new List<ScoreSong>
        {
            new(226, "hard"),
            new(74, "expert"),
            new(448, "expert")
        };
    }

    private async Task ComputeMultiSongScoresAsync(
        List<ScoreSong> songs,
        string suitePath,
        string masterDir,
        string musicMetas,
        string region,
        string liveType,
        int? eventId)
    {
        if (songs.Count == 0 || _results.Count == 0) return;

        var runnerPath = IOPath.Combine(AppContext.BaseDirectory, "Services", "Calc", "deck_recommend_runner.py");
        if (!File.Exists(runnerPath))
        {
            await SetStatusUIAsync($"找不到 runner 脚本: {runnerPath}", isError: true);
            return;
        }

        var pythonExe = ResolvePythonExe();
        int deckIndex = 1;
        foreach (var entry in _results)
        {
            var cardIds = entry.CardPreviews.Select(c => c.CardId).ToList();
            if (cardIds.Count == 0)
            {
                deckIndex++;
                continue;
            }

            var parts = new List<string>();
            foreach (var song in songs)
            {
                await SetStatusUIAsync($"计算 #{deckIndex} song={song.MusicId} ...");
                var outputName = $"deck_score_{deckIndex}_{song.MusicId}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                var outputPath = IOPath.Combine(AppPaths.OutputDeckRecommendDir, outputName);

                var args = new List<string>
                {
                    Quote(runnerPath),
                    "--suite-json", Quote(suitePath),
                    "--master-dir", Quote(masterDir),
                    "--music-metas", Quote(musicMetas),
                    "--region", region,
                    "--live-type", liveType,
                    "--music-id", song.MusicId.ToString(CultureInfo.InvariantCulture),
                    "--music-diff", song.Difficulty,
                    "--target", "score",
                    "--algorithm", "dfs",
                    "--limit", "1",
                    "--fixed-cards", string.Join(",", cardIds),
                    "--output", Quote(outputPath)
                };
                if (eventId.HasValue)
                {
                    args.Add("--event-id");
                    args.Add(eventId.Value.ToString(CultureInfo.InvariantCulture));
                }

                var argLine = string.Join(" ", args);
                var ok = await RunProcessAsync(pythonExe, argLine).ConfigureAwait(true);
                if (ok && File.Exists(outputPath) && TryReadDeckScore(outputPath, out var liveScore, out var score))
                {
                    var title = await GetMusicTitleAsync(song.MusicId).ConfigureAwait(false);
                    parts.Add($"{title}({song.Difficulty}): live={liveScore} score={score}");
                }
                else
                {
                    parts.Add($"{song.MusicId}({song.Difficulty}): N/A");
                }
            }

            await EnqueueOnUIAsync(() =>
            {
                entry.ScoreLine = string.Join(" | ", parts);
            });

            deckIndex++;
        }
    }

    private static bool TryReadDeckScore(string path, out int liveScore, out int score)
    {
        liveScore = 0;
        score = 0;
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("decks", out var decks) || decks.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            var first = decks.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object) return false;
            liveScore = GetInt(first, "live_score");
            score = GetInt(first, "score");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task LoadDeckPreviewsAsync()
    {
        var sem = new SemaphoreSlim(4);
        var tasks = new List<Task>();
        foreach (var entry in _results)
        {
            foreach (var preview in entry.CardPreviews)
            {
                tasks.Add(LoadCardPreviewAsync(preview, sem));
            }
        }
        await Task.WhenAll(tasks);
    }

    private async Task LoadCardPreviewAsync(DeckCardPreview preview, SemaphoreSlim sem)
    {
        await sem.WaitAsync();
        try
        {
            await UpdatePreviewStatusAsync(preview, "Loading...");
            var database = await GetCardDatabaseAsync().ConfigureAwait(false);
            var card = database.FirstOrDefault(c => c.Id == preview.CardId);
            if (card == null)
            {
                await UpdatePreviewStatusAsync(preview, "Card not found");
                return;
            }

            var cardAfterTraining = ShouldUseAfterTrainingCard(preview);
            var starAfterTraining = ShouldUseAfterTrainingStar(preview, card);
            var urls = GetCardImageUrls(card, afterTraining: cardAfterTraining, starAfterTraining: starAfterTraining);
            var attrKey = NormalizeAttrValue(card.Attr);

            var cardPath = _cacheService.GetCardPath(preview.CardId, cardAfterTraining);
            var framePath = _cacheService.GetFramePath(card.Rarity);
            var attrPath = _cacheService.GetAttributePath(attrKey);
            var starPath = _cacheService.GetStarPath(starAfterTraining);

            var cardImage = await LoadOrDownloadBitmapAsync(urls.CharacterImage, cardPath);
            var frameImage = await LoadFrameImageAsync(card.Rarity, urls.FrameImage, framePath);
            var attributeImage = await LoadOrDownloadBitmapAsync(urls.AttributeImage, attrPath);
            var starImage = urls.RarityStars.Count > 0 ? await LoadOrDownloadBitmapAsync(urls.RarityStars[0], starPath) : null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                preview.CardImage = cardImage;
                preview.FrameImage = frameImage;
                preview.AttributeImage = attributeImage;
                preview.RarityStars.Clear();
                if (starImage != null)
                {
                    for (int i = urls.RarityStars.Count - 1; i >= 0; i--)
                    {
                        preview.RarityStars.Add(starImage);
                    }
                }
                preview.LoadStatus = "OK";
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeckRecommend] Load card preview failed: {ex.Message}");
            await UpdatePreviewStatusAsync(preview, "Error");
        }
        finally
        {
            sem.Release();
        }
    }

    private Task UpdatePreviewStatusAsync(DeckCardPreview preview, string status)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            preview.LoadStatus = status;
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                preview.LoadStatus = status;
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private async Task<Bitmap?> LoadOrDownloadBitmapAsync(string url, string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                return new Bitmap(cachePath);
            }

            var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
            Directory.CreateDirectory(IOPath.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
            await using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private async Task<Bitmap?> LoadFrameImageAsync(int rarity, string? remoteUrl, string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                return new Bitmap(cachePath);
            }

            if (rarity >= 1 && rarity <= 3)
            {
                var localPng = IOPath.Combine(AppContext.BaseDirectory, "Assets", "frames", $"frame_{rarity}.png");
                if (File.Exists(localPng))
                {
                    return new Bitmap(localPng);
                }

                var fromBase64 = await TryLoadFrameFromBase64Async(rarity, cachePath);
                if (fromBase64 != null) return fromBase64;
            }

            if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
            return await LoadOrDownloadBitmapAsync(remoteUrl, cachePath);
        }
        catch
        {
            return null;
        }
    }

    private async Task<Bitmap?> TryLoadFrameFromBase64Async(int rarity, string cachePath)
    {
        if (rarity < 1 || rarity > 3) return null;

        var assetBase64 = IOPath.Combine(AppContext.BaseDirectory, "Assets", "frames", $"frame_{rarity}.txt");
        var cacheBase64 = IOPath.Combine(AppPaths.OutputOwnedCardsDir, "FramesCache", $"frame_{rarity}.txt");
        var base64Path = File.Exists(assetBase64) ? assetBase64 : (File.Exists(cacheBase64) ? cacheBase64 : null);
        if (base64Path == null) return null;

        try
        {
            var content = await File.ReadAllTextAsync(base64Path).ConfigureAwait(false);
            var base64 = ExtractBase64Payload(content);
            if (string.IsNullOrWhiteSpace(base64)) return null;

            var bytes = Convert.FromBase64String(base64);
            Directory.CreateDirectory(IOPath.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);

            await using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractBase64Payload(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var trimmed = content.Trim();
        var marker = "base64,";
        var index = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return trimmed[(index + marker.Length)..].Trim();
        }
        return trimmed;
    }

    private async Task<List<DeckRecommendSekaiCard>> GetCardDatabaseAsync()
    {
        if (_cardDatabase != null)
        {
            return _cardDatabase;
        }

        await _cardDatabaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cardDatabase != null)
            {
                return _cardDatabase;
            }

            string json;
            var apiUrl = "https://sekai-world.github.io/sekai-master-db-diff/cards.json";
            try
            {
                json = await _http.GetStringAsync(apiUrl).ConfigureAwait(false);
            }
            catch
            {
                var backupPath = IOPath.Combine(AppContext.BaseDirectory, "Assets", "cards_backup.json");
                json = File.Exists(backupPath) ? await File.ReadAllTextAsync(backupPath).ConfigureAwait(false) : "[]";
            }

            _cardDatabase = ParseCardDatabaseJson(json);
            return _cardDatabase;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeckRecommend] GetCardDatabase failed: {ex.Message}");
            return new List<DeckRecommendSekaiCard>();
        }
        finally
        {
            _cardDatabaseGate.Release();
        }
    }

    private static List<DeckRecommendSekaiCard> ParseCardDatabaseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement cardsArray = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            cardsArray = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("cards", out cardsArray) && cardsArray.ValueKind == JsonValueKind.Array)
            {
            }
            else if (root.TryGetProperty("data", out cardsArray) && cardsArray.ValueKind == JsonValueKind.Array)
            {
            }
        }

        if (cardsArray.ValueKind != JsonValueKind.Array)
        {
            return new List<DeckRecommendSekaiCard>();
        }

        var list = new List<DeckRecommendSekaiCard>();
        foreach (var el in cardsArray.EnumerateArray())
        {
            if (TryParseCard(el, out var card))
            {
                list.Add(card);
            }
        }
        return list;
    }

    private static bool TryParseCard(JsonElement el, out DeckRecommendSekaiCard card)
    {
        card = new DeckRecommendSekaiCard();

        if (!TryGetInt(el, out var id, "id", "cardId", "cardID", "card_id"))
        {
            return false;
        }
        card.Id = id;

        card.AssetbundleName = TryGetString(el, "assetbundleName", "assetBundleName", "assetbundle_name") ?? string.Empty;
        card.Attr = NormalizeAttrValue(TryGetString(el, "attr", "attribute", "cardAttrType", "cardAttributeType", "card_attr", "card_attribute") ?? string.Empty);
        card.SupportUnit = TryGetString(el, "supportUnit", "support_unit") ?? string.Empty;
        card.CardRarityType = TryGetString(el, "cardRarityType", "rarityType") ?? string.Empty;

        if (TryGetInt(el, out var rarity, "rarity"))
        {
            card.Rarity = rarity;
        }
        else if (!string.IsNullOrWhiteSpace(card.CardRarityType))
        {
            card.Rarity = ParseTrailingInt(card.CardRarityType);
        }

        return true;
    }

    private static bool TryGetString(JsonElement el, out string? value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString();
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string? TryGetString(JsonElement el, params string[] keys)
    {
        return TryGetString(el, out var value, keys) ? value : null;
    }

    private static bool TryGetInt(JsonElement el, out int value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!el.TryGetProperty(key, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
            {
                return true;
            }
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out value))
            {
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static int ParseTrailingInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        for (int i = value.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(value[i]))
            {
                var digits = value[(i + 1)..];
                return int.TryParse(digits, out var result) ? result : 0;
            }
        }
        return int.TryParse(value, out var v) ? v : 0;
    }

    private static string NormalizeAttrValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "cool";
        var v = value.Trim().ToLowerInvariant();
        string[] known = { "cute", "cool", "pure", "happy", "mysterious" };
        foreach (var k in known)
        {
            if (v == k) return k;
            if (v.EndsWith("_" + k, StringComparison.Ordinal)) return k;
        }
        return v;
    }

    private static bool ShouldUseAfterTrainingCard(DeckCardPreview preview)
    {
        if (preview.AfterTraining) return true;
        return IsSpecialTraining(preview.DefaultImageType);
    }

    private static bool ShouldUseAfterTrainingStar(DeckCardPreview preview, DeckRecommendSekaiCard card)
    {
        if (IsBirthdayCard(card)) return true;
        if (preview.AfterTraining) return true;
        return IsSpecialTraining(preview.DefaultImageType);
    }

    private static bool IsSpecialTraining(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Trim().Equals("special_training", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBirthdayCard(DeckRecommendSekaiCard card)
    {
        return !string.IsNullOrWhiteSpace(card.CardRarityType) &&
            card.CardRarityType.Contains("birthday", StringComparison.OrdinalIgnoreCase);
    }

    private static DeckRecommendCardImageUrls GetCardImageUrls(DeckRecommendSekaiCard card, bool afterTraining, bool starAfterTraining)
    {
        var suffix = afterTraining ? "_after_training" : "_normal";
        var baseUrl = "https://storage.sekai.best/sekai-jp-assets";
        var characterUrl = $"{baseUrl}/thumbnail/chara/{card.AssetbundleName}{suffix}.webp";
        var isBirthday = IsBirthdayCard(card);
        var frameUrl = isBirthday
            ? "https://sekai.best/assets/cardFrame_S_bd-CrsQsaNc.png"
            : card.Rarity >= 4
                ? "https://sekai.best/assets/cardFrame_S_4-DkwuVAqt.png"
                : string.Empty;

        var attrMap = new Dictionary<string, string>
        {
            ["cute"] = "icon_attribute_cute-BqKuT21a.png",
            ["cool"] = "icon_attribute_cool-Cm_EFAKA.png",
            ["pure"] = "icon_attribute_pure-DMCNUXNX.png",
            ["happy"] = "icon_attribute_happy-POeZUq3N.png",
            ["mysterious"] = "icon_attribute_mysterious-DRt6JUuH.png"
        };
        var attrKey = NormalizeAttrValue(card.Attr);
        var attrIcon = attrMap.GetValueOrDefault(attrKey, "icon_attribute_cool-Cm_EFAKA.png");
        var attributeUrl = $"https://sekai.best/assets/{attrIcon}";

        var starUrl = starAfterTraining
            ? "https://sekai.best/assets/rarity_star_afterTraining-CUlLhfpl.png"
            : "https://sekai.best/assets/rarity_star_normal-BYSplh9m.png";
        var rarityStars = Enumerable.Repeat(starUrl, Math.Max(card.Rarity, 0)).ToList();

        return new DeckRecommendCardImageUrls(characterUrl, frameUrl, attributeUrl, rarityStars);
    }

    private async Task<string> GetMusicTitleAsync(int musicId)
    {
        var map = await GetMusicNameMapAsync().ConfigureAwait(false);
        if (map != null && map.TryGetValue(musicId, out var title))
        {
            return title;
        }
        return musicId.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<Dictionary<int, string>?> GetMusicNameMapAsync()
    {
        if (_musicNameMap != null) return _musicNameMap;

        await _musicNameGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_musicNameMap != null) return _musicNameMap;
            var path = IOPath.Combine(AppContext.BaseDirectory, "Assets", "musics.json");
            if (!File.Exists(path)) return null;

            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var map = new Dictionary<int, string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) &&
                    el.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                {
                    map[id] = titleEl.GetString() ?? id.ToString(CultureInfo.InvariantCulture);
                }
            }
            _musicNameMap = map;
            return _musicNameMap;
        }
        catch
        {
            return null;
        }
        finally
        {
            _musicNameGate.Release();
        }
    }

    private Task EnqueueOnUIAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private void SetStatus(string message, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = message;
            StatusText.Foreground = isError ? Brushes.IndianRed : Brushes.Black;
        });
    }

    private void AppendStatus(string message, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var current = StatusText.Text ?? string.Empty;
            var next = string.IsNullOrWhiteSpace(current) ? message : current + Environment.NewLine + message;
            var lines = next.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length > MaxStatusLines)
            {
                next = string.Join(Environment.NewLine, lines[^MaxStatusLines..]);
            }
            StatusText.Text = next;
            StatusText.Foreground = isError ? Brushes.IndianRed : Brushes.Black;
        });
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

    private static void TryApplyBundledPythonEnv(ProcessStartInfo psi)
    {
        var baseDir = AppContext.BaseDirectory;
        var bundledRoot = IOPath.Combine(baseDir, "python");
        if (!psi.FileName.StartsWith(bundledRoot, StringComparison.Ordinal)) return;

        psi.Environment["DYLD_FRAMEWORK_PATH"] = bundledRoot;
        psi.Environment["DYLD_LIBRARY_PATH"] = IOPath.Combine(bundledRoot, "Python.framework", "Versions", "Current", "lib");
    }
}

public class DeckRecommendEntry : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _summaryLine = string.Empty;
    private string _scoreLine = string.Empty;

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
        }
    }

    public int TotalPower { get; set; }

    public double EventBonusRate { get; set; }

    public string SummaryLine
    {
        get => _summaryLine;
        set
        {
            if (_summaryLine != value)
            {
                _summaryLine = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryLine)));
            }
        }
    }

    public string ScoreLine
    {
        get => _scoreLine;
        set
        {
            if (_scoreLine != value)
            {
                _scoreLine = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScoreLine)));
            }
        }
    }

    public ObservableCollection<DeckCardPreview> CardPreviews { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class DeckCardPreview : INotifyPropertyChanged
{
    private Bitmap? _cardImage;
    private Bitmap? _frameImage;
    private Bitmap? _attributeImage;
    private string _loadStatus = "Pending";

    public int CardId { get; set; }

    public bool AfterTraining { get; set; }

    public string DefaultImageType { get; set; } = string.Empty;

    public int SkillScoreUp { get; set; }

    public Bitmap? CardImage
    {
        get => _cardImage;
        set
        {
            if (_cardImage != value)
            {
                _cardImage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardImage)));
            }
        }
    }

    public Bitmap? FrameImage
    {
        get => _frameImage;
        set
        {
            if (_frameImage != value)
            {
                _frameImage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FrameImage)));
            }
        }
    }

    public Bitmap? AttributeImage
    {
        get => _attributeImage;
        set
        {
            if (_attributeImage != value)
            {
                _attributeImage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AttributeImage)));
            }
        }
    }

    public ObservableCollection<Bitmap> RarityStars { get; } = new();

    public string LoadStatus
    {
        get => _loadStatus;
        set
        {
            if (_loadStatus != value)
            {
                _loadStatus = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LoadStatus)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal class DeckRecommendSekaiCard
{
    public int Id { get; set; }
    public string AssetbundleName { get; set; } = string.Empty;
    public int Rarity { get; set; }
    public string Attr { get; set; } = string.Empty;
    public string SupportUnit { get; set; } = string.Empty;
    public string CardRarityType { get; set; } = string.Empty;
}

internal record DeckRecommendCardImageUrls(
    string CharacterImage,
    string FrameImage,
    string AttributeImage,
    List<string> RarityStars);

internal record ScoreSong(int MusicId, string Difficulty);
