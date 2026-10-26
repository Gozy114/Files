# Everything search and folder size

Personal-use integration on the [Gozy114/Files](https://github.com/Gozy114/Files) fork of current `main`. Do not open a pull request against `files-community/Files` and do not push to upstream.

Primary reference: closed, unmerged [files-community/Files#17336](https://github.com/files-community/Files/pull/17336) (`elliotttate:feature/everything-search-folder-size`, head `688f2782`). Related: issue [#5845](https://github.com/files-community/Files/issues/5845). The app-extension sketch in [#18930](https://github.com/files-community/Files/issues/18930) (`Files.EverythingFolderSizeProvider`) is not the approach. Current `main` has no extension host for folder sizes. In-app SDK use matches #17336 and is the fit for this fork.

## Goals

- Optional voidtools Everything integration for search and folder size.
- A Folders settings toggle, default off, with a safe fallback when Everything is absent.
- A Debug build that does not need the Everything service at compile time.
- A GitHub Actions workflow on `windows-latest` that restores and builds Debug.

## Non-goals

- Any pull request or push to `files-community/Files`.
- An app-extension host, a second search-engine settings page, or a user-editable result cap.
- Rewriting Windows Search, tag search, or natural-language search.
- Shipping Everything 1.5 SDK3 binaries.
- A functional Everything test on Actions. Hosted runners have no Everything service.
- Treating a local Windows SDK install as a required setup step for the user.

## Settings

- New `IFoldersSettingsService.UseEverything` (`bool`), persisted with the other folder settings, default `false`.
- One toggle on the Folders settings page. Strings live in `Strings/en-US/Resources.resw` (the source generator only reads that file).
- Toggle off: Windows search and the existing `DrivesSizeProvider` / `CachedSizeProvider` path. No Everything calls.
- Toggle on, Everything responding: Everything is used for ordinary search and for folder sizes. Show a short note that Everything should index file size, and on 1.5 folder size.
- Toggle on, Everything not installed or not running: InfoBar with a link to `https://www.voidtools.com/`. Search and folder size stay on the Windows path.
- Changing the toggle clears the active size provider so the next enumeration picks the new backend.

## Fallback

Search (`FolderSearch.SearchAsync`) calls Everything only when all of the following hold:

- `UseEverything` is true and the client reports available.
- The query is not a Files tag query (`tag:`) and not AQS (leading `$`, or a colon query such as `System.FileName:`).
- The scope is not a library. Libraries stay on the existing walk. `Home` is an unscoped Everything query.

Zero results, an IPC error, a missing database, or any other failure falls through to the existing Windows search path. Suggestions keep `MaxItemCount`.

Folder size (`UserSizeProvider`) uses `EverythingSizeProvider` only when the toggle is on and Everything is available. Otherwise it keeps `DrivesSizeProvider`. A failed Everything query must not publish `0` as a final size. It falls back to an iterative filesystem walk (heap stack, skip reparse points, remember visited paths). A successful query that finds no files is an empty folder and may publish `0`.

Availability is cached for a short interval and then rechecked, so starting or stopping Everything does not require an app restart. A missing or unloadable DLL is remembered and logged once.

## SDK

Ship the SDK2 client libraries already attached to #17336:

- `src/Files.App/Libraries/Everything32.dll`
- `src/Files.App/Libraries/Everything64.dll`
- `src/Files.App/Libraries/EverythingARM64.dll`

Copy them to the output the same way the 7z DLLs are copied. They are IPC clients, not the Everything service, so the project still compiles when Everything is not installed.

Load with `NativeLibrary` and `delegate* unmanaged[Stdcall]` function pointers. Do not add `DllImport`. Pick the DLL from `RuntimeInformation.ProcessArchitecture` (`Arm64`, then 64-bit, then 32-bit).

SDK2 keeps one process-global query. Every SDK2 call goes through a single gate, off the UI thread. Do not call `Everything_CleanUp` (the PR hit access violations). Use the wide exports that the DLLs actually contain: `Everything_SetSearchW`, `Everything_QueryW`, `Everything_GetResultFileNameW`, `Everything_GetResultPathW`, and the matching size, date, and attribute getters. The unsuffixed `Everything_GetResultFileName` and `Everything_GetResultPath` names used by the PR are not exported.

Search requests name, path, size, created, modified, and attributes. Cap results at 1000, or at `MaxItemCount` for suggestions.

Folder size query is files under that folder and its descendants: `file:` plus a quoted path that ends with a separator. Page with `Everything_SetMax` / `Everything_SetOffset`. Add a result only when its full path is still under the requested folder. If paging hits the internal cap, treat the size as unknown and use the filesystem walk instead of showing a partial total.

SDK3 (`Everything3_GetFolderSizeFromFilenameW`) is optional and only when `Everything3_x64.dll`, `Everything3_ARM64.dll`, or `Everything3_x86.dll` is already on the machine (an Everything 1.5 install). `ulong.MaxValue` means unknown and falls back to the SDK2 sum. Do not port the PR's `Everything3_CreateQuery` / `GetResultPathW` search calls. Those entry points are not in the current [SDK3 header](https://github.com/voidtools/everything_sdk3). Do not log a missing SDK3 DLL once per folder.

## Fixes for the #17336 review

| Report | What went wrong | What this port does |
| --- | --- | --- |
| Folder sizes jump between folders on refresh (Josh65-2201) | SDK2 state is global. Concurrent size and search calls read each other's results. `folder:"path" file:` is not a recursive file query (`folder:` matches folders), so the sum was often 0 and then replaced by another calculation. Sort value `13` is date-modified, not size. | One gate. Path check before adding a size. `file:` plus a trailing-separator path. Successful empty result stays 0. Failed query does not cache 0. |
| Stack overflow around the recursive fallback with Everything 1.4 (Josh65-2201, about line 292) | A bad query returned 0, then `FallbackCalculateAsync` recursed with `DirectoryInfo` and `await`, which runs synchronously until it yields. Everything IPC via `SendMessage` can also re-enter the UI thread. | Iterative fallback. SDK calls only on a background thread, inside the gate. |
| Log spam: `SDK3 DLL not found` on every folder | Each size request constructed a new SDK3 client and logged the miss. | Probe SDK3 once per process. |
| ARM64 does not work (yair100) | Everything 1.5 alpha has no ARM64 build. The PR's resolver and unsuffixed entry points also failed to bind. | Load `EverythingARM64.dll` for an ARM64 process. Search and the SDK2 size sum use that client. |

## Known limitations

- Everything 1.5 alpha has no ARM64 build. An ARM64 Files process can talk to Everything 1.4 through `EverythingARM64.dll`. An x64-only Everything 1.5 install may not answer that client. That case uses the Windows fallback.
- SDK3 search from the PR is not ported. Search uses SDK2, which Everything 1.5 still serves.
- 1.4 folder size is the sum of indexed file sizes, not size on disk, and it skips anything Everything does not index. Enabling "Index file size" in Everything is required for non-zero SDK2 sums. 1.5 folder-size indexing is used only when SDK3 is present and returns a real value.
- Trees past the page cap use the filesystem walk.
- Libraries are not searched or sized through Everything.
- Tag queries and AQS stay on Windows Search.
- No test in this repo calls the Everything service.

## CI

Add `.github/workflows/fork-debug-build.yml`.

- `runs-on: windows-latest`
- `push` and `pull_request` on this fork
- No `github.repository_owner == 'files-community'` condition. The existing `Files CI` workflow has that condition, so it never runs here, and it uses the `windows-2025-vs2026` runner rather than GitHub-hosted `windows-latest`.
- Restore `Files.slnx`, then build `src/Files.App/Files.App.csproj` with `Configuration=Debug`, `Platform=x64`, and `GenerateAppxPackageOnBuild=false`.
- Green means the Debug compile succeeded. It does not mean Everything search or folder size was executed.
- If the first run fails, fix it on this same branch from the Actions log until Debug passes, or record the environmental blocker in this spec and in the fork PR.

## References

- https://github.com/files-community/Files/pull/17336
- https://github.com/files-community/Files/issues/5845
- https://github.com/files-community/Files/issues/18930
- https://www.voidtools.com/support/everything/sdk/
- https://github.com/voidtools/everything_sdk3
