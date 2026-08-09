using System;
using System.IO;

namespace ProsekaTools.Avalonia.Services;

public static class AppPaths
{
    private const string AppFolderName = "ProsekaTools";

    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    public static string GetCapturesRoot() => Path.Combine(AppDataRoot, "captures");

    public static string GetCapturesCategoryDir(string category)
        => Path.Combine(GetCapturesRoot(), category);

    public static string CapturesMysekaiDir => GetCapturesCategoryDir("mysekai");

    public static string CapturesSuiteDir => GetCapturesCategoryDir("suite");

    public static string OutputRoot => Path.Combine(AppDataRoot, "output");

    public static string OutputMysekaiDir => Path.Combine(OutputRoot, "mysekai");

    public static string OutputOwnedCardsDir => Path.Combine(OutputRoot, "owned_cards");

    public static string OutputDeckRecommendDir => Path.Combine(OutputRoot, "deck_recommend");

    public static void EnsureDir(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }
}
