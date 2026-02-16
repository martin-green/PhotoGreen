# PhotoGreen — Raw Image Editor Plan

**Status: All 6 phases complete.**

## Architecture Overview

```
PhotoGreen.App
??? MainWindow (Shell: 3-panel layout)
?   ??? FileBrowserPanel   (left)
?   ??? ImageViewerPanel   (center)
?   ??? EditingPanel       (right)
?
??? Services
?   ??? FileGroupingService   ? scans dirs, groups RAW+JPG by stem
?   ??? ThumbnailService      ? extracts/caches embedded JPEG previews
?   ??? ImageLoaderService    ? loads raw at 16-bit linear via Magick.NET
?   ??? RawDevelopmentEngine  ? applies develop settings ? display bitmap
?   ??? AutoAdjustEngine      ? histogram analysis ? auto DevelopSettings
?
??? Models
    ??? ImageFileGroup     ? primary (raw) + sidecar (jpg) grouped by name
    ??? ImageFileEntry     ? single file: path, type, size, date
    ??? DevelopSettings    ? immutable record of all editing parameters
```

## Folder Structure

```
PhotoGreen/
??? App.xaml / App.xaml.cs
??? MainWindow.xaml / MainWindow.xaml.cs
??? Models/
?   ??? ImageFileGroup.cs
?   ??? ImageFileEntry.cs
?   ??? DevelopSettings.cs
??? ViewModels/
?   ??? MainViewModel.cs
?   ??? FileBrowserViewModel.cs
?   ??? ImageViewerViewModel.cs
?   ??? EditingViewModel.cs
??? Views/
?   ??? FileBrowserView.xaml
?   ??? ImageViewerView.xaml
?   ??? EditingPanelView.xaml
??? Services/
?   ??? FileGroupingService.cs
?   ??? ThumbnailService.cs
?   ??? ImageLoaderService.cs
?   ??? RawDevelopmentEngine.cs
?   ??? AutoAdjustEngine.cs
??? Converters/
?   ??? BitmapToImageSourceConverter.cs
??? Themes/
    ??? DarkTheme.xaml
```

## NuGet Packages

| Package | Purpose |
|---|---|
| `Magick.NET-Q16-AnyCPU` | Raw decoding, image manipulation |
| `CommunityToolkit.Mvvm` | ObservableObject, RelayCommand, source generators |

---

## Phase 1 — Project Foundation & File Browser

| Task | Detail |
|---|---|
| 1.1 Project setup | Add NuGet packages |
| 1.2 MVVM structure | Create folders: Models/, ViewModels/, Views/, Services/, Converters/ |
| 1.3 File browser model | `ImageFileGroup` holds primary file (prefer raw) + optional sidecar(s) |
| 1.4 `FileGroupingService` | Scan directory, group by stem name, expose ObservableCollection |
| 1.5 File browser UI | TreeView for folders + ListView with thumbnails for grouped images |
| 1.6 Thumbnail generation | Extract embedded JPEG preview from raw EXIF for fast thumbnails |

## Phase 2 — Image Viewer & Raw Loading

| Task | Detail |
|---|---|
| 2.1 `ImageLoaderService` | Load raw at full 16-bit linear depth via MagickImage |
| 2.2 Linear pipeline | Read raw as 16-bit linear to preserve full dynamic range |
| 2.3 Image viewer panel | Zoomable, pannable viewer with WriteableBitmap for live updates |
| 2.4 Histogram | Live RGB histogram rendered with DrawingVisual |

## Phase 3 — Editing Controls (Develop Module)

| Task | Detail |
|---|---|
| 3.1 `DevelopSettings` model | Immutable record: Exposure, Contrast, Highlights, Shadows, Whites, Blacks, Temperature, Tint, Vibrance, Saturation, Sharpness, NoiseReduction |
| 3.2 Tone mapping | Exposure (EV stops), Contrast (S-curve), Highlights, Shadows, Whites, Blacks via tone curves |
| 3.3 Color balance | Temperature (blue?amber) and Tint (green?magenta) via channel multipliers |
| 3.4 HSL panel | Per-channel Hue/Saturation/Luminance |
| 3.5 Sharpening | Unsharp mask via MagickImage.UnsharpMask() |
| 3.6 UI sliders | Labeled Slider controls bound to DevelopSettings |

## Phase 4 — Raw Development Pipeline

| Task | Detail |
|---|---|
| 4.1 `RawDevelopmentEngine` | 16-bit linear pixels + DevelopSettings ? 8-bit sRGB WriteableBitmap |
| 4.2 Tone curve builder | 65536-entry LUT from tone parameters for O(1) per-pixel mapping |
| 4.3 Performance | CPU with Parallel.For over scanlines |
| 4.4 Live preview | Debounce slider changes (~50ms), apply on background thread |

### Pipeline Flow

```
16-bit Linear RAW
  ? White Balance (channel multiply)
  ? Exposure (EV multiply)
  ? Tone Curve LUT (Highlights/Shadows/Contrast/Whites/Blacks)
  ? HSL Adjustments
  ? Linear ? sRGB Gamma
  ? Sharpening
  ? 8-bit WriteableBitmap
```

## Phase 5 — Auto Adjust (Lightroom-style)

| Task | Detail |
|---|---|
| 5.1 Histogram analysis | Compute luminance histogram from linear data |
| 5.2 Auto exposure | Target median luminance at ~18% gray; compute EV offset |
| 5.3 Auto contrast | 1st/99th percentile ? set Whites/Blacks |
| 5.4 Auto highlights/shadows | Top/bottom quartile analysis ? recover detail |
| 5.5 Auto white balance | Gray-world assumption: equalize average R, G, B |
| 5.6 "Auto" button | Single click ? compute all ? populate DevelopSettings |

## Phase 6 — Export & Polish

| Task | Detail |
|---|---|
| 6.1 Export | Save as JPEG/TIFF/PNG with quality settings |
| 6.2 Sidecar files | Save/load DevelopSettings as JSON .pgr sidecars |
| 6.3 Undo/Redo | Stack of DevelopSettings snapshots |
| 6.4 Keyboard shortcuts | R reset, A auto, Ctrl+Z undo, Ctrl+S export |
| 6.5 Dark theme | WPF resource dictionary for dark photo-editing theme |
