# Everything integration tasks

Work on `cursor/everything-search-folder-size-4681` in [Gozy114/Files](https://github.com/Gozy114/Files) only. Spec: [SPEC.md](./SPEC.md).

Do not open a pull request against `files-community/Files`. After each task (or a pair of tasks), tick the box, commit, push, and stop for review.

- [x] **T1. SDK interop wrapper.** Add the three SDK2 client DLLs under `src/Files.App/Libraries/` and copy them to the output from `Files.App.csproj`. Add a dynamically loaded wrapper (`NativeLibrary` plus stdcall function pointers, no `DllImport`) that selects `Everything32.dll`, `Everything64.dll`, or `EverythingARM64.dll` from the process architecture, binds the wide exports, and runs every SDK2 call on one background gate. Expose availability (cached, logged once on failure), search, and paged folder-size sum. Optionally probe `Everything3_*` for `Everything3_GetFolderSizeFromFilenameW` only when that DLL is already installed. Do not port the PR's SDK3 search entry points.

- [x] **T2. Folder size provider.** Add `EverythingSizeProvider` and select it from `UserSizeProvider` only when `UseEverything` is on and the client is available. Cache by path. Publish a successful sum, including 0. On IPC failure, an unloaded database, or the page cap, fall back to an iterative filesystem walk. Never recurse with `async`/`await`. Switch providers when the toggle changes. The settings property can land with T4 if T2 needs a temporary read; the UI stays in T4.

- [x] **T3. Search integration.** From `FolderSearch.SearchAsync`, call Everything when the spec's gates pass (toggle, availability, not tag, not AQS, not a library). Map hits to `ListedItem` and raise `SearchTick` with the current `IReadOnlyList<ListedItem>` signature. On zero hits or any failure, fall through to the existing Windows search path. Leave tag, AQS, library, and natural-language behavior unchanged.

- [ ] **T4. Settings toggle and UI.** `UseEverything` is already on `IFoldersSettingsService` and `FoldersSettingsService`, default `false`. Add the Folders page toggle, the unavailable InfoBar with the voidtools download link, and the indexing hint when Everything responds. Strings in `Strings/en-US/Resources.resw` only.

- [ ] **T5. windows-latest workflow.** Add `.github/workflows/fork-debug-build.yml` that, on `push` and `pull_request`, restores `Files.slnx` and builds `src/Files.App/Files.App.csproj` Debug x64 on `windows-latest` with packaging turned off. No `files-community` owner guard. No Everything functional test.

- [ ] **T6. CI green.** Push, read the Actions log, and fix this branch until the Debug build passes. If the runner cannot supply the toolchain, record that blocker in `SPEC.md` and stop retrying.

- [ ] **T7. PR on the fork.** Open a pull request on `Gozy114/Files` with base `main`. Describe what was ported, the settings toggle, the known limitations, and how to open the Actions run. Do not open or retarget a PR to `files-community/Files`.
