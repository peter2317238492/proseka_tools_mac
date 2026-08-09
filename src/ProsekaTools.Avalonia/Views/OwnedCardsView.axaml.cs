using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

public sealed partial class OwnedCardsView : UserControl
{
    private const int CompactUserCardCardIdIndex = 0;
    private const int CompactUserCardDefaultImageIndex = 9;

    private string? _selectedFile;
    private string? _lastLoadedJson;
    private readonly ObservableCollection<OwnedCardEntry> _cards = new();
    private readonly OwnedCardCacheService _cache = new();
    private readonly SemaphoreSlim _cardDatabaseGate = new(1, 1);
    private List<SekaiCard>? _cardDatabase;
    private int _cardLoadVersion;

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static OwnedCardsView()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7");
        _http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        _http.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
    }

    public OwnedCardsView()
    {
        InitializeComponent();
        CardsItems.ItemsSource = _cards;

        ChooseFileButton.Click += async (_, _) => await ChooseFileAsync();
        StartDecryptButton.Click += async (_, _) => await StartDecryptAsync();
        LoadLatestJsonButton.Click += async (_, _) => await LoadLatestJsonAsync();
        ReloadCardsButton.Click += async (_, _) => await ReloadCardsAsync();
    }

    private async Task ChooseFileAsync()
    {
        if (UseLatestCheckBox.IsChecked == true)
        {
            var latest = TryGetLatestCapture();
            if (latest is null)
            {
                SetStatus("未在 captures/suite 找到文件。", true);
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

    private string? TryGetLatestJson()
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

            var region = (RegionCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "jp";
            var outputDir = AppPaths.OutputOwnedCardsDir;
            Directory.CreateDirectory(outputDir);
            var outName = IOPath.GetFileNameWithoutExtension(inputFile) + ".json";
            var outputPath = IOPath.Combine(outputDir, outName);

            var (exePath, args) = BuildSssekaiCommand(inputFile, outputPath, region);
            if (exePath is null || args is null)
            {
                SetStatus("未找到内置 Python 或 sssekai 模块。", true);
                return;
            }

            var ok = await RunProcessAsync(exePath, args);
            if (ok && File.Exists(outputPath))
            {
                SetStatus($"解密完成: {outputPath}");
                await LoadJsonAndPopulateCardsAsync(outputPath);
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

    private async Task LoadLatestJsonAsync()
    {
        var latest = TryGetLatestJson();
        if (latest == null)
        {
            SetStatus("未在 output/owned_cards 找到 JSON。", true);
            return;
        }
        await LoadJsonAndPopulateCardsAsync(latest);
    }

    private async Task ReloadCardsAsync()
    {
        if (!string.IsNullOrWhiteSpace(_lastLoadedJson) && File.Exists(_lastLoadedJson))
        {
            await LoadJsonAndPopulateCardsAsync(_lastLoadedJson);
        }
        else
        {
            await LoadLatestJsonAsync();
        }
    }

    private async Task LoadJsonAndPopulateCardsAsync(string path)
    {
        try
        {
            _lastLoadedJson = path;
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetUserCards(root, out var cardsArray))
            {
                SetStatus("JSON 缺少 userCards。", true);
                return;
            }

            var cardDefaults = new Dictionary<int, string>();
            foreach (var el in cardsArray.EnumerateArray())
            {
                if (!TryGetCardId(el, out var id)) continue;
                var defaultImage = TryGetUserCardDefaultImage(el) ?? string.Empty;
                if (!cardDefaults.ContainsKey(id))
                {
                    cardDefaults[id] = defaultImage;
                }
                else if (IsSpecialTraining(defaultImage))
                {
                    cardDefaults[id] = defaultImage;
                }
            }

            _cards.Clear();
            foreach (var id in cardDefaults.Keys.OrderBy(i => i))
            {
                cardDefaults.TryGetValue(id, out var defaultImage);
                _cards.Add(new OwnedCardEntry
                {
                    CardId = id,
                    DefaultImageType = defaultImage ?? string.Empty,
                    LoadStatus = "Pending"
                });
            }

            SetStatus($"已加载 JSON: {IOPath.GetFileName(path)} (cards: {cardDefaults.Count})");
            await LoadCardImagesAsync(_cards.ToList());
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private static bool TryGetUserCards(JsonElement root, out JsonElement cardsArray)
    {
        if (root.TryGetProperty("userCards", out cardsArray) && cardsArray.ValueKind == JsonValueKind.Array) return true;
        if (root.TryGetProperty("updatedResources", out var updated) && updated.TryGetProperty("userCards", out cardsArray) && cardsArray.ValueKind == JsonValueKind.Array) return true;
        cardsArray = default;
        return false;
    }

    private static bool TryGetCardId(JsonElement el, out int cardId)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            return TryGetArrayInt(el, CompactUserCardCardIdIndex, out cardId);
        }

        string[] keys = { "cardId", "cardID", "CardId", "CardID" };
        foreach (var key in keys)
        {
            if (el.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out cardId))
            {
                return true;
            }
        }
        cardId = 0;
        return false;
    }

    private static string? TryGetUserCardDefaultImage(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            return TryGetArrayString(el, CompactUserCardDefaultImageIndex);
        }

        return TryGetString(el, "defaultImage", "defaultImageType");
    }

    private static bool TryGetArrayInt(JsonElement el, int index, out int value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() <= index)
        {
            return false;
        }

        var item = el[index];
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out value))
        {
            return true;
        }

        if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static string? TryGetArrayString(JsonElement el, int index)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() <= index)
        {
            return null;
        }

        var item = el[index];
        return item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    }

    private async Task LoadCardImagesAsync(List<OwnedCardEntry> cards)
    {
        int version = Interlocked.Increment(ref _cardLoadVersion);
        var sem = new SemaphoreSlim(4);
        var tasks = cards.Select(c => LoadCardImageAsync(c, sem, version)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task LoadCardImageAsync(OwnedCardEntry entry, SemaphoreSlim sem, int version)
    {
        await sem.WaitAsync();
        try
        {
            if (version != _cardLoadVersion) return;
            entry.LoadStatus = "Loading...";

            var database = await GetCardDatabaseAsync().ConfigureAwait(false);
            var card = database.FirstOrDefault(c => c.Id == entry.CardId);
            if (card == null)
            {
                entry.LoadStatus = "Card not found";
                return;
            }

            var cardAfterTraining = ShouldUseAfterTrainingCard(entry);
            var starAfterTraining = ShouldUseAfterTrainingStar(entry, card);
            var urls = GetCardImageUrls(card, afterTraining: cardAfterTraining, starAfterTraining: starAfterTraining);

            var cardPath = _cache.GetCardPath(entry.CardId, cardAfterTraining);
            var framePath = _cache.GetFramePath(card.Rarity);
            var attrKey = NormalizeAttrValue(card.Attr);
            var attrPath = _cache.GetAttributePath(attrKey);
            var starPath = _cache.GetStarPath(starAfterTraining);

            var cardImage = await LoadOrDownloadBitmapAsync(urls.CharacterImage, cardPath);
            var frameImage = await LoadFrameImageAsync(card.Rarity, urls.FrameImage, framePath);
            var attributeImage = await LoadOrDownloadBitmapAsync(urls.AttributeImage, attrPath);
            var starImage = urls.RarityStars.Count > 0 ? await LoadOrDownloadBitmapAsync(urls.RarityStars[0], starPath) : null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                entry.CardImage = cardImage;
                entry.FrameImage = frameImage;
                entry.AttributeImage = attributeImage;
                entry.RarityStars.Clear();
                if (starImage != null)
                {
                    for (int i = urls.RarityStars.Count - 1; i >= 0; i--)
                    {
                        entry.RarityStars.Add(starImage);
                    }
                }
                entry.LoadStatus = "OK";
            });
        }
        catch (Exception ex)
        {
            entry.LoadStatus = $"Error: {ex.GetType().Name}";
            Debug.WriteLine(ex.Message);
        }
        finally
        {
            sem.Release();
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = message;
            StatusText.Foreground = isError ? Brushes.IndianRed : Brushes.Black;
        });
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

    private async Task<List<SekaiCard>> GetCardDatabaseAsync()
    {
        if (_cardDatabase != null) return _cardDatabase;

        await _cardDatabaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cardDatabase != null) return _cardDatabase;

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
        catch
        {
            return new List<SekaiCard>();
        }
        finally
        {
            _cardDatabaseGate.Release();
        }
    }

    private static CardImageUrls GetCardImageUrls(SekaiCard card, bool afterTraining, bool starAfterTraining)
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

        return new CardImageUrls(characterUrl, frameUrl, attributeUrl, rarityStars);
    }

    private static bool ShouldUseAfterTrainingStar(OwnedCardEntry entry, SekaiCard card)
    {
        if (IsBirthdayCard(card)) return true;
        return IsSpecialTraining(entry.DefaultImageType);
    }

    private static bool ShouldUseAfterTrainingCard(OwnedCardEntry entry)
    {
        return IsSpecialTraining(entry.DefaultImageType);
    }

    private static bool IsBirthdayCard(SekaiCard card)
    {
        return !string.IsNullOrWhiteSpace(card.CardRarityType) &&
               card.CardRarityType.Contains("birthday", StringComparison.OrdinalIgnoreCase);
    }

    private static List<SekaiCard> ParseCardDatabaseJson(string json)
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
            return new List<SekaiCard>();
        }

        var list = new List<SekaiCard>();
        foreach (var el in cardsArray.EnumerateArray())
        {
            if (TryParseCard(el, out var card))
            {
                list.Add(card);
            }
        }
        return list;
    }

    private static bool TryParseCard(JsonElement el, out SekaiCard card)
    {
        card = new SekaiCard();

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

    private static bool IsSpecialTraining(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Trim().Equals("special_training", StringComparison.OrdinalIgnoreCase);
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

    private static void TryApplyBundledPythonEnv(ProcessStartInfo psi)
    {
        var baseDir = AppContext.BaseDirectory;
        var bundledRoot = IOPath.Combine(baseDir, "python");
        if (!psi.FileName.StartsWith(bundledRoot, StringComparison.Ordinal)) return;

        psi.Environment["DYLD_FRAMEWORK_PATH"] = bundledRoot;
        psi.Environment["DYLD_LIBRARY_PATH"] = IOPath.Combine(bundledRoot, "Python.framework", "Versions", "Current", "lib");
    }
}

internal class CardImageUrls
{
    public CardImageUrls(string characterImage, string frameImage, string attributeImage, List<string> rarityStars)
    {
        CharacterImage = characterImage;
        FrameImage = frameImage;
        AttributeImage = attributeImage;
        RarityStars = rarityStars;
    }

    public string CharacterImage { get; }
    public string FrameImage { get; }
    public string AttributeImage { get; }
    public List<string> RarityStars { get; }
}

internal class SekaiCard
{
    public int Id { get; set; }
    public string AssetbundleName { get; set; } = string.Empty;
    public string Attr { get; set; } = string.Empty;
    public string SupportUnit { get; set; } = string.Empty;
    public string CardRarityType { get; set; } = string.Empty;
    public int Rarity { get; set; }
}

public class OwnedCardEntry : INotifyPropertyChanged
{
    public int CardId { get; set; }
    public string DefaultImageType { get; set; } = string.Empty;

    private Bitmap? _cardImage;
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

    private Bitmap? _frameImage;
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

    private Bitmap? _attributeImage;
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

    private string _loadStatus = "Pending";
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

    public ObservableCollection<Bitmap?> RarityStars { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
}
