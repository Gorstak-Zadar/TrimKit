# Changelog

## 0.1.0 — 2026-08-16

### Architecture & Code Quality Improvements

- **Shared ProcessRunner utility** — extracted common process execution (DISM, PowerShell) into `ProcessRunner.cs` with concurrent stdout/stderr reading to prevent pipe deadlocks, CancellationToken support with process-kill on cancel
- **INotifyPropertyChanged on models** — `WindowsPackage`, `WindowsFeature`, `RegistryTweak`, `RemovableComponent`, `WindowsServiceInfo` now use `ObservableObject` + `[ObservableProperty]` so UI checkboxes update correctly on programmatic changes (e.g. preset load)
- **IDialogService abstraction** — all MessageBox, OpenFileDialog, SaveFileDialog, FolderBrowserDialog calls in ViewModels replaced with an `IDialogService` interface for testability and separation of concerns
- **Dropped WinForms dependency** — replaced `System.Windows.Forms.FolderBrowserDialog` with `Microsoft.Win32.OpenFolderDialog` (.NET 8+), removed `<UseWindowsForms>` from csproj
- **CancellationToken support** — `IDismService` methods now accept `CancellationToken`; MainViewModel exposes a Cancel command that kills running DISM processes
- **ApplyService extraction** — NTLite component map execution, wallpaper application, and preset service changes moved from MainViewModel (~120 lines) into a dedicated `ApplyService`
- **SafeFireAndForget extension** — replaces `_ = SomeAsync()` pattern with logged exception handling via `TaskExtensions.SafeFireAndForget()`
- **Fixed empty catch blocks** — added logging to silent catches in MainViewModel; annotated intentional cleanup catches
- **Fixed hardcoded mount path** — default changed from `C:\TrimKitMount` to `Path.Combine(Path.GetTempPath(), "TrimKitMount")` for portability
- **Installer build script** — updated to support Inno Setup 7

## 0.0.6 — 2026-06-15

### Added
- **Interactive WIM Unmount Recovery** — Refactored cleanup logic to catch save and unmount failures, offering interactive Retry, Discard, or Cancel options to protect customized images from locks.
- **Batch Update Integration** — Group updates by SSU and cumulative/other updates, integrating them in batches to reduce integration time significantly.
- **Unsigned Driver Option** / **Force Unsigned Drivers Checkbox** — Added a checkbox in the Drivers tab to append the `/ForceUnsigned` parameter to DISM.
- **Secure Tooling** — Disabled runtime downloading of unverified `oscdimg.exe` binaries, securely falling back to native UDF creation using PowerShell/IMAPI2.
- **Baseline en-US & Critical Font Safeguards** — Protected the baseline `en-US` language packs, capabilities, and critical system fonts/cache (Segoe, Arial, Marlett, Symbol, and the `StaticCache.dat` font cache) from direct or preset-based removal.

### Fixed
- **Cyrillic Typo in Component Removal** — Resolved search pattern typo from `"kbд*.dll"` to Latin `"kbd*.dll"` to correctly find all keyboard layouts.
- **CA2024 Blocking Warning** in `UupDumpService` replaced with asynchronous stream reading.
- **Global /English Flag** — Prepend `/English` globally to all DISM process arguments to ensure output parsing remains locale-independent regardless of the host system.

## 0.0.5 — 2026-06-15

### Added
- **WinReducer wallpaper import** — parses Appearance section (Desktop Wallpaper, Setup Screen, Lock Screen paths) and applies during Apply All
- **WinReducer service import** — parses Services section (disable/remove with Value=4/5), maps 65+ display names to service IDs, applies during Apply All
- **Components tab with categories** — grouped by type (Apps, Capabilities, Fonts, Keyboards, Languages, Drivers) with expandable sections
- **Select All per category** — one-click buttons to select all items within a group
- **NTLite component map at scale** — handles 1000+ items: `driver_*.inf` → DriverStore removal, `kl-*` → keyboard DLL, `font_*` → font file, `lang*` → language tag, `microsoft.*` → app removal
- **Service loading fallback** — if registry hive copy fails (DISM lock), falls back to filesystem-based detection (scans `drivers\*.sys`)
- **Preset wallpaper/service fields** — `WallpaperPreset` and `ServicePreset` model classes stored in `.wwp` format

### Fixed
- Services tab no longer shows "0 services" — uses filesystem fallback when hive is locked
- Preset save now includes ALL selected components from ALL tabs (apps, capabilities, fonts, keyboards, languages, drivers)
- Apply confirmation dialog shows correct component count across all categories
- NTLite/WinReducer preset loading matches items by both ID and display name
- Preset loading is additive (load multiple presets, selections accumulate)

### Changed
- Components tab replaced flat list with categorized expandable view
- Apply pipeline now includes: wallpaper injection → service changes → NTLite component map execution → DISM cleanup
- WinReducer import handles all category types (Remove, Features, Services, Appearance, System)

## 0.0.4 — 2026-06-15

### Added
- **Autounattend Generator tab** — full interactive UI: locale, username/password, OOBE skip, TPM/SecureBoot/RAM bypass, Defender/UAC/hibernation disable, Compact OS, auto-logon
- **Compatibility tab** — live checkbox lists for all 45+ SafetyGuard protection areas and 12 BootWimSafetyGuard protections (check = protected from removal)
- **ISO build at end of Apply All** — Save As dialog asks where to output the final trimmed ISO, builds with oscdimg
- **Microsoft Update Catalog integration** — Fetch Available Updates button scrapes catalog.update.microsoft.com filtered to mounted edition + build + architecture
- **Update eligibility check** — verifies WinSxS integrity, servicing stack, CBS database before allowing updates (blocks if previously debloated)
- **Services tab lazy loading** — scans on-demand when tab is selected (copies SYSTEM hive to temp to avoid DISM lock)
- Framework package filter — Apps tab hides runtime dependencies (VCLibs, .NET Native, UI.Xaml, WindowsAppRuntime) that break other apps if removed

### Fixed
- **Services scan no longer hangs** — copies SYSTEM hive to temp before reg load (DISM holds lock on mounted hive)
- **Services tab shows 0 results** — fixed by copying hive, proper reg QUERY parsing, and timeout handling
- Apps tab was showing framework/runtime packages that don't exist as user-facing apps — now filtered
- Apply All no longer just cleans up — it saves WIMs, reassembles ISO structure, and builds final ISO
- Compatibility options and boot protections now loaded at startup and bound to UI

### Changed
- Apply All pipeline: debloat → unmount+commit → copy WIMs to sources → Save As dialog → build ISO → cleanup
- Services load lazily (not during mount) to prevent blocking the UI
- All placeholder tabs replaced with real interactive content

## 0.0.3 — 2026-06-15

### Added
- Full ISO workflow: Browse → mount (suppressed Explorer) → copy to NTFS temp → pick edition → extract → debloat
- Separate boot.wim and install.wim extraction into dedicated work folders
- Three-tier safety system: `SafetyGuard` (install.wim), `BootWimSafetyGuard` (boot.wim), `IsoFileSafetyGuard` (ISO files)
- ISO file-level debloating with safety (removes upgrade agent, appraiser, unused MUI, keeps bootloader/EFI/BCD)
- ComponentRemovalService wired into Apply All (Appx apps, capabilities, fonts, keyboards, languages, inbox drivers)
- WinSxS cleanup runs after component removal (StartComponentCleanup + ResetBase)
- DISM image cleanup (shrinks WIM after stripping)
- Service configuration in Apply All flow
- Interactive tab content: Apps, Fonts, Keyboards, Languages, Drivers, Features, Services, Components, Tweaks
- All tabs show checkbox lists bound to live data from mounted image
- Tab switching via sidebar (SelectedTabIndex binding)
- Graceful cleanup on app close (always dismounts, deletes temp folder)
- Emergency sync cleanup on crash/process exit (ProcessExit + UnhandledException handlers)
- `ForceCleanupSync()` — synchronous last-resort cleanup using raw dism.exe/PowerShell
- `IsBusy` guard on all long-running commands (prevents double-execution)
- `BrowseWimAsync` — proper async command (no fire-and-forget)
- Admin elevation via manifest (`requireAdministrator`)
- `installer/build.ps1` — automated build script (clean → publish → Inno Setup)
- `InverseBoolToVisibilityConverter` for state-aware tab content
- `IndexToVisibilityConverter` for tab panel switching

### Fixed
- Sidebar tabs now switch content panels (was showing only Source/ISO tab)
- Tab panels show correct state text based on whether image is mounted
- Fire-and-forget async calls replaced with proper awaits
- Assembly name in manifest corrected from "WimWitch" to "TrimKit"

### Changed
- Apply All now runs the full pipeline: packages → features → tweaks → drivers → component removal → services → WinSxS cleanup → DISM cleanup
- Both install.wim and boot.wim debloated in parallel with independent safety guards
- Close behavior: always cleans up without dialog (discards unsaved)

---

## 0.0.2 — 2026-06-14

### Added
- ISO mounting via Explorer shell verb (reliable drive letter assignment)
- Auto ESD→WIM conversion (detects recovery LZMS format)
- UUP dump and Microsoft download integration (in-app API + open-in-browser)
- Full autounattend.xml generator
- Customization service (wallpapers, branding, scripts, $OEM$, cursors)
- WinSxS cleanup service
- Component removal service (all NTLite/WinReducer removal categories)
- Service manager (offline registry manipulation)
- Image tools (edition management, WIM↔ESD, ISO building)
- Preset system with NTLite/WinReducer import and combine
- 100+ registry tweaks database
- SafetyGuard with 45+ compatibility protection areas
- Chrome-dark theme with Mica backdrop (Win11)

---

## 0.0.1 — 2026-06-12

- Initial release
- Basic WIM mount/unmount
- Package listing and removal
- Feature enable/disable
- Driver injection
