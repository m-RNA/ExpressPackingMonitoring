# Repository Guidelines

## Project Structure & Module Organization

- `ExpressPackingMonitoring.sln` is the main solution.
- `ExpressPackingMonitoring/` contains the WPF application, including XAML views, view models, services, SQLite access, recording logic, and `Web/index.html`.
- `ExpressPackingMonitoring.Launcher/` contains the small launcher executable used by the clean package layout.
- `Tools/Publish-CleanPackage.ps1` creates the distributable directory, full zip, update manifest, launcher manifest, and optional AppPatch package.
- `Scripts/快递助手订单推送.user.js` is the browser userscript for order push integration.
- `Image/` stores README and project screenshots. `Test/HTML/` contains captured sample pages for script/debug reference, not an automated test suite.

## Build, Test, and Development Commands

```powershell
dotnet restore ExpressPackingMonitoring.sln
dotnet build ExpressPackingMonitoring.sln -c Debug
dotnet run --project ExpressPackingMonitoring
pwsh -NoProfile -File Tools\Publish-CleanPackage.ps1
```

- `restore` downloads NuGet dependencies.
- `build` verifies the WPF app and launcher compile.
- `run` starts the main app locally.
- `Tools\Publish-CleanPackage.ps1` produces the clean release layout with the root launcher and `app\` payload.

## Runtime and Distribution Notes

- The publish script generates a directory package and a matching `.zip`.
- The clean package root should mainly contain `ExpressPackingMonitoring.exe` and `app\`; the real app payload, dependencies, Web files, LibVLC files, and `tools\ffmpeg.exe` live under `app\`.
- Release packages must not include `config.json`, `videos.db`, cache files, logs, recordings, or other local runtime data.
- Runtime data is stored under `%LOCALAPPDATA%\ExpressPackingMonitoring\`, so normal upgrades keep user configuration and database records.
- `ffmpeg.exe` may be resolved from `app\tools\ffmpeg.exe`, the application runtime directory, or the system `PATH`.
- `Scripts/快递助手订单推送.user.js` is the browser userscript used for order push integration.
- Edge TTS is the default online voice path. Kokoro local TTS models and runtime dependencies are optional and should not be bundled unless explicitly intended.
- Full packages include the generated default Edge TTS cache. AppPatch packages must exclude TTS cache files.

## Update & Release Workflow

- Users should start the root launcher. The launcher starts the app immediately, checks updates in the background, downloads verified AppPatch packages into `%LOCALAPPDATA%\ExpressPackingMonitoring\cache\updates`, and installs pending patches on the next launcher run.
- The launcher must not update itself. If launcher source or project configuration changes, disable AppPatch for that release and require a full package update.
- AppPatch packages are fixed-baseline cumulative patches. The default patch baseline is `0.0.18`, but scripts may allow overriding it when a new formal baseline is chosen.
- Keep update URLs configurable through environment variables or `.env`. The default update check URL is GitHub releases latest API; `.env` may point to another release provider.
- Do not generate AppFull packages. Release uploads normally include the full zip, `update_vX.Y.Z.json`, optional `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`, and `launcher_manifest_vX.Y.Z.json`.
- Keep release notes in `update_vX.Y.Z.json` synchronized with the final release description before uploading.
- GitHub releases receive all generated upload artifacts. Gitee releases receive the update JSON, optional AppPatch, and launcher manifest, but not the full package zip.
- For Gitee, open the new-release page for the user and let the user complete the form and upload files manually; do not automate submission unless the user explicitly changes this workflow.
- Do not update ExpressPackingMonitoring.Launcher unless necessary

## Storage, Cache, and Web Video

- Storage settings are expressed as reserved free space for the system and other apps, not as a recording quota. Keep `StorageSpacePolicy` as the single source of truth for minimum reserve rules.
- Cache-like Web artifacts, including transcode cache, clip previews, and clipped downloads, live under `%LOCALAPPDATA%\ExpressPackingMonitoring\cache` and are cleaned by the Web cache limit.
- Web clipping is named “剪辑” / “剪辑并下载”. Do not call it “导出视频”, which can be confused with original video download.

## Coding Style & Naming Conventions

Use C# with nullable references and implicit usings enabled. Follow the existing WPF/MVVM style: `PascalCase` for public types, properties, and commands; `camelCase` for locals; `_camelCase` for private fields. Keep XAML names descriptive and aligned with their backing view or view model. Preserve UTF-8 text and avoid broad line-ending or encoding churn, especially in Chinese strings, XAML, HTML, and userscript files. UI copy should not be followed by a Chinese period or English period at the end of the sentence.

## Testing Guidelines

`ExpressPackingMonitoring.Tests/` contains the automated regression suite. At minimum, run `dotnet test ExpressPackingMonitoring.Tests/ExpressPackingMonitoring.Tests.csproj -c Debug` and `dotnet build ExpressPackingMonitoring.sln -c Debug` before committing. For recording, Web playback, TTS, packaging, or FFmpeg changes, also run the affected workflow manually and note what was verified. Use `Test/HTML/` pages when validating userscript parsing behavior.

Before every release, run `pwsh -NoProfile -File Tools/Test-Release.ps1`. Packaging is blocked unless all required core business and recovery tests are present and passing and the manual scenarios in `RELEASE_CHECKLIST.md` have been completed. Do not confirm `-ConfirmManualCoreChecks` without performing those real-device checks.

## Commit & Pull Request Guidelines

Recent history uses conventional prefixes with Chinese subjects, for example `fix: 优化 Web 搜索和转码确认` and `docs: 优化 README 表述`. Keep commits scoped and include a short body explaining what changed and why. Do not include secrets, local paths, account IDs, signing files, or machine-specific details.

Pull requests should include a concise summary, validation steps, linked issue if applicable, and screenshots or recordings for UI, playback, or packaging changes.

## Security & Configuration Tips

Do not commit generated configs, databases, logs, caches, recordings, `.env` files, certificates, or signing material. Runtime data belongs under `%LOCALAPPDATA%\ExpressPackingMonitoring\`; release packages should not include local user state.
