# Repository Guidelines

## Project Structure & Module Organization

- `ExpressPackingMonitoring.sln` is the main solution.
- `ExpressPackingMonitoring/` contains the WPF application, including XAML views, view models, services, SQLite access, recording logic, and `Web/index.html`.
- `ExpressPackingMonitoring.Launcher/` contains the small launcher executable used by the clean package layout.
- `Tools/Publish-CleanPackage.ps1` creates the per-user Setup, distributable directory, LZMA2 solid 7z, compatibility zip, update manifest, launcher manifest, and optional AppPatch package.
- `Scripts/快递助手订单推送.user.js` is the browser userscript for order push integration.
- `Image/` stores README and project screenshots. `Test/HTML/` contains captured sample pages for script/debug reference, not an automated test suite.

## Build, Test, and Development Commands

```powershell
dotnet restore ExpressPackingMonitoring.sln
dotnet build ExpressPackingMonitoring.sln -c Debug
dotnet run --project ExpressPackingMonitoring
pwsh -NoProfile -File Tools\Publish-CleanPackage.ps1
pwsh -NoProfile -File Tools\Test-Release-Automated.ps1
```

- `restore` downloads NuGet dependencies.
- `build` verifies the WPF app and launcher compile.
- `run` starts the main app locally.
- `Tools\Publish-CleanPackage.ps1` produces the clean release layout with the root launcher and `app\` payload.
- `Tools\Test-Release-Automated.ps1` runs the isolated WPF smoke test, userscript concurrency/routing tests, and headless Web UI acceptance suite.

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
- Do not generate AppFull packages. GitHub Release uploads normally include the Setup, full 7z, compatibility zip, `update_vX.Y.Z.json`, and optional `ExpressPackingMonitoring_AppPatch_vX.Y.Z.zip`.
- Keep release notes in `update_vX.Y.Z.json` synchronized with the final release description before uploading.
- Keep `launcher_manifest_vX.Y.Z.json` and `release_info_vX.Y.Z.txt` as local verification and handoff files; do not upload them to GitHub or Gitee by default.
- Gitee releases receive the update JSON and optional AppPatch, but not the Setup, full package 7z, or full package zip.
- For Gitee, open the new-release page for the user and let the user complete the form and upload files manually; do not automate submission unless the user explicitly changes this workflow.
- Do not update ExpressPackingMonitoring.Launcher unless necessary

## Storage, Cache, and Web Video

- Storage settings are expressed as reserved free space for the system and other apps, not as a recording quota. Keep `StorageSpacePolicy` as the single source of truth for minimum reserve rules.
- Cache-like Web artifacts, including transcode cache, clip previews, and clipped downloads, live under `%LOCALAPPDATA%\ExpressPackingMonitoring\cache` and are cleaned by the Web cache limit.
- Web clipping is named “剪辑” / “剪辑并下载”. Do not call it “导出视频”, which can be confused with original video download.

## Destructive File Operation Safety

- Treat deletion of recordings, databases, configuration, update payloads, and generated outputs as concurrency-sensitive. Before deleting, verify the exact file owner, lifecycle state, and current source/target relationship under the same synchronization used to create or replace it.
- A failed task must not delete a shared output merely because that output exists. Another task may have completed successfully and removed or replaced the source before the failed task observes it.
- Keep incomplete-output cleanup inside the owning operation and lock. Only remove an output when the original source is still preserved and the current operation can prove that it created the incomplete file.
- Add a regression test for destructive or replacement logic that exercises the competing-task ordering: task A completes and publishes the target, then task B reaches failure cleanup. The test must verify that task B preserves task A's valid target.
- Prefer recoverable cleanup or explicit database deletion records where practical. Log the reason and exact target for every automatic deletion of material data.

## Coding Style & Naming Conventions

Use C# with nullable references and implicit usings enabled. Follow the existing WPF/MVVM style: `PascalCase` for public types, properties, and commands; `camelCase` for locals; `_camelCase` for private fields. Keep XAML names descriptive and aligned with their backing view or view model. Preserve UTF-8 text and avoid broad line-ending or encoding churn, especially in Chinese strings, XAML, HTML, and userscript files. UI copy should not be followed by a Chinese period or English period at the end of the sentence.

## Testing Guidelines

`ExpressPackingMonitoring.Tests/` contains the automated regression suite. At minimum, run `dotnet test ExpressPackingMonitoring.Tests/ExpressPackingMonitoring.Tests.csproj -c Debug` and `dotnet build ExpressPackingMonitoring.sln -c Debug` before committing. For recording, Web playback, TTS, packaging, or FFmpeg changes, also run the affected workflow manually and note what was verified. Use `Test/HTML/` pages when validating userscript parsing behavior.

Before every release, run `pwsh -NoProfile -File Tools/Test-Release-Automated.ps1`; packaging remains blocked unless the automated checks pass. The real-device scenarios in `RELEASE_CHECKLIST.md` are recommended but non-blocking, and any unverified scenarios must be reported with the release. Do not pass `-ConfirmManualCoreChecks` unless those real-device checks were actually performed.

## Commit & Pull Request Guidelines

Recent history uses conventional prefixes with Chinese subjects, for example `fix: 优化 Web 搜索和转码确认` and `docs: 优化 README 表述`. Keep commits scoped and include a short body explaining what changed and why. Do not include secrets, local paths, account IDs, signing files, or machine-specific details.

Pull requests should include a concise summary, validation steps, linked issue if applicable, and screenshots or recordings for UI, playback, or packaging changes.

## Security & Configuration Tips

Do not commit generated configs, databases, logs, caches, recordings, `.env` files, certificates, or signing material. Runtime data belongs under `%LOCALAPPDATA%\ExpressPackingMonitoring\`; release packages should not include local user state.
