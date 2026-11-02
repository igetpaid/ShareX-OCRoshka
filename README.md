# ShareX OCRoshka

**Unofficial fork of [ShareX](https://github.com/ShareX/ShareX) 21.0.0 with genuinely better
OCR.** The built-in Windows OCR is replaced by Tesseract 5.4.0 with Russian and English
(`rus+eng`) in one pass, bundled into the portable build. Nothing else about ShareX changes.

Русский: неофициальный форк ShareX с улучшенным распознаванием текста. Встроенный Windows OCR
заменён на Tesseract 5.4.0 (`rus+eng`), Tesseract уже лежит внутри архива. Проверка обновлений
отключена, чтобы официальное обновление не затёрло патч.

## Download

Take the latest **`ShareX-21.0.0-portable-x64.zip`** from
[Releases](https://github.com/igetpaid/ShareX-OCRoshka/releases/latest), unpack it anywhere and
run `ShareX.exe`. No installer and no .NET runtime needed.

## What is different

- `ShareX/Tools/OCR/OCRHelper.cs` calls the OCRoshka engine (`OCRoshka.Engine`) instead of
  `Windows.Media.Ocr`. If Tesseract is missing, it falls back to the original Windows OCR.
- `OCRoshka.Engine/` — the recognition engine: Tesseract CLI, adaptive second pass, and path
  repair that only changes a path when the file system confirms it.
- `ShareX/SystemOptions.cs` — update checks are always disabled. An official ShareX update would
  replace this build and silently remove the improved OCR.
- The portable package includes `tesseract/` next to `ShareX.exe`.

## Build

Requires .NET SDK 9 and Windows.

```powershell
dotnet build ShareX.sln -c Release -p:Platform=x64 --self-contained true
```

Put Tesseract 5.4.0 with `tessdata_best` into `ShareX\bin\Release\win-x64\tesseract`, then:

```powershell
ShareX.Setup\bin\Release\win-x64\ShareX.Setup.exe -job CreatePortable -platform x64
```

The result is `Output\ShareX-21.0.0-portable-x64.zip`.

## Legal

This is an unofficial fork, not affiliated with or endorsed by the ShareX Team. ShareX is
licensed under GPL-3.0; this fork keeps the same license and all original copyright notices.
Tesseract OCR is Apache-2.0; language data comes from `tessdata_best` (Apache-2.0).

---

<p align="center"><a href="https://getsharex.com"><img src="https://getsharex.com/img/ShareX_Banner.png" alt="ShareX Banner"/></a></p>
<h3 align="center">Screen capture, file sharing and productivity tool</h3>
<br>
<div align="center">
  <a href="https://github.com/ShareX/ShareX/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/ShareX/ShareX/build.yml?branch=develop&label=Build&cacheSeconds=3600" alt="GitHub Workflow Status"/></a>
  <a href="./LICENSE.txt"><img src="https://img.shields.io/github/license/ShareX/ShareX?label=License&color=brightgreen&cacheSeconds=3600" alt="License"/></a>
  <a href="https://github.com/ShareX/ShareX/releases/latest"><img src="https://img.shields.io/github/v/release/ShareX/ShareX?label=Release&color=brightgreen&cacheSeconds=3600" alt="Release"/></a>
  <a href="https://getsharex.com/downloads"><img src="https://img.shields.io/github/downloads/ShareX/ShareX/total?label=Downloads&cacheSeconds=3600" alt="Downloads"/></a>
  <a href="https://discord.gg/ShareX"><img src="https://img.shields.io/discord/194170124859736065?label=Discord&cacheSeconds=3600" alt="Discord"/></a>
  <a href="https://twitter.com/intent/follow?screen_name=ShareX"><img src="https://img.shields.io/twitter/follow/ShareX?cacheSeconds=3600" alt="Twitter"/></a>
</div>
<br>
<p align="center"><a href="https://getsharex.com"><img src="https://getsharex.com/img/ShareX_Screenshot.png" alt="ShareX Screenshot"/></a></p>
<p align="center">For further information please check our <a href="https://getsharex.com">website</a></p>

## Links
* Official website: https://getsharex.com
* GitHub: https://github.com/ShareX/ShareX
* Changelog: https://getsharex.com/changelog
* Privacy policy: https://getsharex.com/privacy-policy
* Donate: https://getsharex.com/donate
* Twitter: https://twitter.com/ShareX
* Discord: https://discord.gg/ShareX
* Reddit: https://www.reddit.com/r/sharex
* Steam page: https://store.steampowered.com/app/400040/ShareX/
* Microsoft Store page: https://apps.microsoft.com/detail/9nblggh4z1sp

## Documents
* Image effects: https://getsharex.com/image-effects
* Actions: https://getsharex.com/actions
* Dev builds: https://getsharex.com/docs/dev-builds
* Keybinds: https://getsharex.com/docs/keybinds
* Scrolling screenshot: https://getsharex.com/docs/scrolling-screenshot
* Command line arguments: https://getsharex.com/docs/command-line-arguments
* Translation: https://getsharex.com/docs/translation
* OCR: https://getsharex.com/docs/ocr
* Custom uploader: https://getsharex.com/docs/custom-uploader
* Amazon S3 guide: https://getsharex.com/docs/amazon-s3
* Google Cloud Storage guide: https://getsharex.com/docs/google-cloud-storage
* Cloudflare R2 guide: https://getsharex.com/docs/cloudflare-r2
* Brand assets: https://getsharex.com/brand-assets

## Star History
<a href="https://www.star-history.com/?repos=ShareX%2FShareX&type=date&legend=bottom-right">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=ShareX/ShareX&type=date&theme=dark&legend=bottom-right" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=ShareX/ShareX&type=date&legend=bottom-right" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=ShareX/ShareX&type=date&legend=bottom-right" />
 </picture>
</a>
