using System.IO;

namespace ProsekaTools.Avalonia.Services;

public sealed class OwnedCardCacheService
{
    private readonly string _cardDir;
    private readonly string _frameDir;
    private readonly string _attrDir;
    private readonly string _starDir;

    public OwnedCardCacheService()
    {
        _cardDir = Path.Combine(AppPaths.OutputOwnedCardsDir, "ImageCache");
        _frameDir = Path.Combine(AppPaths.OutputOwnedCardsDir, "FramesCache");
        _attrDir = Path.Combine(AppPaths.OutputOwnedCardsDir, "AttributesCache");
        _starDir = Path.Combine(AppPaths.OutputOwnedCardsDir, "StarsCache");

        Directory.CreateDirectory(_cardDir);
        Directory.CreateDirectory(_frameDir);
        Directory.CreateDirectory(_attrDir);
        Directory.CreateDirectory(_starDir);
    }

    public string GetCardPath(int cardId, bool afterTraining)
        => Path.Combine(_cardDir, $"card_{cardId}_{(afterTraining ? "after" : "normal")}.webp");

    public string GetFramePath(int rarity)
        => Path.Combine(_frameDir, $"frame_{rarity}.png");

    public string GetAttributePath(string attr)
        => Path.Combine(_attrDir, $"attr_{attr}.png");

    public string GetStarPath(bool afterTraining)
        => Path.Combine(_starDir, $"star_{(afterTraining ? "after" : "normal")}.png");
}
