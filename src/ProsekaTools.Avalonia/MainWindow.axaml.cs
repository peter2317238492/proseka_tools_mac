using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ProsekaTools.Avalonia.Views;

namespace ProsekaTools.Avalonia;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Control> _pages;
    private readonly Dictionary<string, (string Title, string Subtitle)> _pageMeta;
    private readonly Dictionary<string, Button> _navButtons;

    public MainWindow()
    {
        InitializeComponent();

        _pages = new Dictionary<string, Control>
        {
            ["home"] = new HomeView(),
            ["grab"] = new GrabDataView(),
            ["suite"] = new SuiteUploadView(),
            ["mysekai"] = new MysekaiView(),
            ["cards"] = new OwnedCardsView(),
            ["deck"] = new DeckRecommendView()
        };

        _pageMeta = new Dictionary<string, (string Title, string Subtitle)>
        {
            ["home"] = ("主页", "常用工具与最近操作"),
            ["grab"] = ("数据抓取", "采集请求并查看服务状态"),
            ["suite"] = ("suite 工具", "上传捕获并生成可用数据"),
            ["mysekai"] = ("Mysekai 工具", "解密数据与地图查看"),
            ["cards"] = ("已拥有的卡片", "解密并校验卡片资源"),
            ["deck"] = ("组卡器", "配置参数并执行推荐")
        };

        _navButtons = new Dictionary<string, Button>
        {
            ["home"] = NavHomeButton,
            ["grab"] = NavGrabButton,
            ["suite"] = NavSuiteButton,
            ["mysekai"] = NavMysekaiButton,
            ["cards"] = NavOwnedCardsButton,
            ["deck"] = NavDeckButton
        };

        SwitchPage("home");
    }

    private void OnNavClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string key)
        {
            return;
        }

        SwitchPage(key);
    }

    private void SwitchPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            return;
        }

        ContentHost.Content = page;

        if (_pageMeta.TryGetValue(key, out var meta))
        {
            PageTitleText.Text = meta.Title;
            PageSubtitleText.Text = meta.Subtitle;
        }

        foreach (var navButton in _navButtons.Values)
        {
            navButton.Classes.Remove("active");
        }

        if (_navButtons.TryGetValue(key, out var selectedButton))
        {
            selectedButton.Classes.Add("active");
        }
    }
}
