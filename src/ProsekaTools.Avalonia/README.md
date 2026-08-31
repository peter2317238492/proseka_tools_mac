# ProsekaTools.Avalonia

已迁移的功能：Tab1（数据抓取）与 Tab2（suite 上传）。
正在迁移：Tab3（Mysekai 工具）。

## 运行方式

```bash
# 在 repo 根目录

dotnet restore ProsekaTools.Avalonia.slnx

dotnet run --project src/ProsekaTools.Avalonia/ProsekaTools.Avalonia.csproj
```

## 说明

- 抓包服务监听 `http://0.0.0.0:8000/`。
- 抓包保存目录：`~/Library/Application Support/ProsekaTools/captures/<category>`。
- suite 上传目标与原 WinUI 版本一致，通过环境变量 `PROSEKA_UPLOAD_BASE` 配置（`$PROSEKA_UPLOAD_BASE/uploadTwSuite`）。
- Mysekai 解密使用内置 macOS Python 分发运行 `sssekai`（`python -m sssekai apidecrypt <infile> <outfile> --region <region>`），请将 Python 分发放到 `src/ProsekaTools.Avalonia/python/`，并确保已安装 `sssekai` 包。

## 待办

- Mysekai/OwnedCards/DeckRecommend 迁移。
- macOS 版 `sssekai` 与 `sekai_deck_recommend_cpp`。
- WebP 解码与图像缓存（Skia/Avalonia 版）。
