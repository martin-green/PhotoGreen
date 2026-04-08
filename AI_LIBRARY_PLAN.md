# PhotoGreen — AI Image Library Plan

**Goal:** Add AI-powered image recognition (face detection, scene description), duplicate detection, and a library viewer with grouping — all integrated into the existing PhotoGreen raw editor.

---

## Architecture Overview

```
PhotoGreen.App
├── MainWindow (Shell: TabControl with Edit & Library tabs)
│   ├── Tab 1: Edit View (existing 3-panel layout, unchanged)
│   │   ├── FileBrowserPanel   (left)
│   │   ├── ImageViewerPanel   (center)
│   │   └── EditingPanel       (right)
│   │
│   └── Tab 2: Library View (new)
│       ├── LibrarySidebarPanel     (left: folder picker + group-by selector)
│       ├── LibraryGridPanel        (center: thumbnail grid with grouping)
│       └── LibraryDetailPanel      (right: image info, faces, tags, actions)
│
├── Services
│   ├── (existing services unchanged)
│   ├── AIService                  → connects to Ollama via OllamaSharp + Microsoft.Extensions.AI
│   ├── FaceRecognitionService    → face detection/clustering using AI vision model
│   ├── ImageDescriptionService   → scene/object tagging via llama3.2-vision
│   ├── DuplicateDetectionService → perceptual hashing + file-hash for exact & near duplicates
│   ├── LibraryScanService        → recursive folder scan, orchestrates analysis, persists data
│   └── LibraryDataStore          → reads/writes the library JSON file in the root folder
│
├── Models
│   ├── (existing models unchanged)
│   ├── LibraryData.cs            → root object serialized to JSON
│   ├── LibraryImageInfo.cs       → per-image: path, faces, tags, hashes, description
│   ├── FaceCluster.cs            → named face group with list of image references
│   ├── DuplicateGroup.cs         → set of images detected as duplicates
│   └── ImageHash.cs              → perceptual hash + file hash for an image
│
└── ViewModels
    ├── (existing view models unchanged)
    ├── LibraryViewModel.cs       → main VM for the library tab
    ├── LibrarySidebarViewModel.cs → folder selection, group-by mode, scan trigger
    └── LibraryDetailViewModel.cs  → detail panel for selected image/group
```

---

## NuGet Packages to Add

| Package | Purpose |
|---|---|
| `OllamaSharp` | .NET client for local Ollama API |
| `Microsoft.Extensions.AI` | Abstraction layer for AI chat/vision models |
| `Microsoft.Extensions.AI.Ollama` | Ollama provider for Microsoft.Extensions.AI |

> **No additional packages needed for duplicate detection** — perceptual hashing (average hash / difference hash) will be implemented using the already-referenced `Magick.NET` for image resizing/grayscale, and SHA-256 from `System.Security.Cryptography` for exact-match hashing.

---

## Data Storage

All library recognition data is stored in a single JSON file at the **root of the scanned library folder**:

```
LibraryRoot/
├── .photogreen-library.json    ← library data file
├── Vacation2024/
│   ├── IMG_001.ARW
│   ├── IMG_002.JPG
│   └── ...
├── Portraits/
│   └── ...
└── ...
```

### JSON Schema (`LibraryData`)

```json
{
  "version": 1,
  "rootFolder": "C:/Photos/MyLibrary",
  "lastScanUtc": "2025-01-15T10:30:00Z",
  "images": [
    {
      "relativePath": "Vacation2024/IMG_001.ARW",
      "fileSize": 25431024,
      "lastModifiedUtc": "2024-07-10T14:22:00Z",
      "perceptualHash": "a4c3b2f1e8d70956",
      "fileHash": "sha256:abcdef1234...",
      "faces": [
        { "label": null, "confidence": 0.92, "boundingBox": { "x": 120, "y": 80, "w": 200, "h": 250 } }
      ],
      "description": "A woman standing on a beach at sunset",
      "tags": ["beach", "sunset", "person", "ocean"],
      "analyzedUtc": "2025-01-15T10:31:00Z"
    }
  ],
  "faceClusters": [
    {
      "id": "face-001",
      "name": "Alice",
      "representativeImagePath": "Portraits/IMG_042.JPG",
      "imagePaths": ["Vacation2024/IMG_001.ARW", "Portraits/IMG_042.JPG"]
    }
  ],
  "duplicateGroups": [
    {
      "id": "dup-001",
      "type": "Exact",
      "imagePaths": ["Vacation2024/IMG_003.JPG", "Backup/IMG_003_copy.JPG"]
    }
  ]
}
```

---

## Phase 7 — Tab View & Main Window Restructure

Convert the existing single-view `MainWindow` into a `TabControl` with two tabs: **Edit** and **Library**.

| Task | Detail |
|---|---|
| 7.1 Wrap existing layout | Move the current `MainWindow` Grid content into a `TabItem` labeled "✏️ Edit" |
| 7.2 Add Library tab | Add a second `TabItem` labeled "📚 Library" with a placeholder Grid |
| 7.3 Tab styling | Style the `TabControl` to match the dark theme — minimal chrome, icon labels |
| 7.4 Navigation integration | When double-clicking an image in the Library grid, switch to Edit tab and load that image |
| 7.5 Shared state | `MainViewModel` holds both `EditingViewModel` (existing) and `LibraryViewModel` (new); the FileBrowser folder can seed the library root |

### UI Layout — Library Tab

```
┌──────────────────────────────────────────────────────────┐
│  [✏️ Edit]  [📚 Library]                                  │
├────────────┬─────────────────────────────┬───────────────┤
│  Sidebar   │  Thumbnail Grid             │  Detail Panel │
│            │                             │               │
│  📁 Root:  │  ┌─────┐ ┌─────┐ ┌─────┐   │  Selected:    │
│  [Browse]  │  │     │ │     │ │     │   │  IMG_001.ARW  │
│            │  │ img │ │ img │ │ img │   │               │
│  Group by: │  │     │ │     │ │     │   │  Faces: 2     │
│  ○ Folder  │  └─────┘ └─────┘ └─────┘   │  Tags: beach  │
│  ○ Faces   │  ┌─────┐ ┌─────┐ ┌─────┐   │    sunset     │
│  ○ Dupes   │  │     │ │     │ │     │   │               │
│  ○ Tags    │  │ img │ │ img │ │ img │   │  [Open Edit]  │
│            │  │     │ │     │ │     │   │  [Delete]     │
│  [Scan]    │  └─────┘ └─────┘ └─────┘   │               │
│  Progress: │                             │  Description: │
│  ████░░ 60%│                             │  "A woman..." │
└────────────┴─────────────────────────────┴───────────────┘
```

---

## Phase 8 — AI Service Layer

Set up the AI communication layer using `OllamaSharp` and `Microsoft.Extensions.AI`.

| Task | Detail |
|---|---|
| 8.1 `AIService` | Singleton service wrapping an `IChatClient` via `Microsoft.Extensions.AI.Ollama`. Configurable endpoint (default `http://localhost:11434`). Model: `llama3.2-vision` |
| 8.2 Connection check | Method to verify Ollama is running and the model is available; surface status in UI |
| 8.3 `AnalyzeImageAsync` | Send an image (as base64 JPEG thumbnail) with a structured prompt; parse JSON response for faces, tags, description |
| 8.4 Prompt engineering | System prompt instructs the model to return structured JSON with fields: `faces` (count + bounding box estimates), `description` (one sentence), `tags` (list of keywords) |
| 8.5 Rate limiting | Process images sequentially or with limited concurrency (1–2) to avoid overwhelming local Ollama |
| 8.6 Error handling | Graceful fallback if Ollama is unavailable — library still works without AI data, just no faces/tags |

### Prompt Design

```
System: You are an image analysis assistant. Analyze the provided image and return ONLY a JSON object with these fields:
- "faceCount": number of human faces visible
- "faces": array of objects with "description" (e.g. "adult male, brown hair"), "approximate_position" ("left", "center", "right")
- "description": one-sentence scene description
- "tags": array of 3-10 descriptive keyword tags

User: [image attachment] Analyze this image.
```

---

## Phase 9 — Duplicate Detection

Implement duplicate detection using perceptual hashing (no AI needed — fast and deterministic).

| Task | Detail |
|---|---|
| 9.1 `ImageHash` model | Stores a 64-bit perceptual hash (average hash) and SHA-256 file hash |
| 9.2 Perceptual hash (aHash) | Using Magick.NET: resize to 8×8, convert to grayscale, compute mean, generate 64-bit hash from above/below mean pixels |
| 9.3 Difference hash (dHash) | Alternative: 9×8 grayscale, compare adjacent pixels for 64-bit hash (better gradient sensitivity) |
| 9.4 File hash | SHA-256 of file bytes for exact duplicate detection |
| 9.5 Hamming distance | Compare perceptual hashes; distance ≤ 5 = near duplicate, distance 0 = perceptual identical |
| 9.6 `DuplicateDetectionService` | Scan all images, compute hashes, group by exact match (SHA-256) and near match (Hamming distance on pHash) |
| 9.7 `DuplicateGroup` model | Holds list of image paths, duplicate type (Exact / NearDuplicate), and a similarity score |
| 9.8 Incremental scanning | Only hash new/modified files (compare file size + last modified against stored data) |

### Algorithm Flow

```
For each image in library:
  1. Check if already hashed (size + date unchanged) → skip
  2. Compute SHA-256 file hash
  3. Load image → resize to 8×8 grayscale → compute aHash (64-bit)
  4. Store both hashes in LibraryImageInfo

After all images hashed:
  5. Group by identical SHA-256 → Exact duplicates
  6. For remaining, compare aHash with Hamming distance ≤ 5 → Near duplicates
  7. Store DuplicateGroups in LibraryData
```

---

## Phase 10 — Face Recognition & Clustering

Use the AI vision model for face detection, then cluster faces across images.

| Task | Detail |
|---|---|
| 10.1 Face detection via AI | Send each image to llama3.2-vision asking for face count and descriptions |
| 10.2 `FaceRecognitionService` | Orchestrates face detection across the library; stores results per image |
| 10.3 Face clustering (AI-assisted) | For images with faces, send pairs/groups of face crops to the model asking "are these the same person?" |
| 10.4 Manual cluster naming | Users can name face clusters (e.g., "Alice", "Bob") via the detail panel |
| 10.5 Cluster merge/split | UI actions to merge two clusters or move a face to a different cluster |
| 10.6 `FaceCluster` model | Named group with representative thumbnail and list of image paths |
| 10.7 Incremental analysis | Only analyze new/modified images; preserve existing cluster assignments |

### Clustering Strategy

Since llama3.2-vision doesn't produce face embeddings, we use a description-based + user-assisted approach:

1. **Detection pass**: AI identifies faces in each image with descriptions ("adult female, blonde hair, glasses")
2. **Initial clustering**: Group faces by similar AI descriptions using string similarity
3. **Refinement pass**: For ambiguous groups, send side-by-side image pairs to AI: "Are these the same person? Answer yes/no"
4. **User correction**: User names clusters and can drag-drop faces between clusters; corrections are persisted

---

## Phase 11 — Library Scan & Data Persistence

| Task | Detail |
|---|---|
| 11.1 `LibraryDataStore` | Read/write `.photogreen-library.json` using `System.Text.Json` (already used by `SidecarService`) |
| 11.2 `LibraryScanService` | Recursively enumerate image files in the library folder and subfolders |
| 11.3 Incremental scan | Compare file size + last modified date; only process new/changed files |
| 11.4 Scan orchestration | For each new image: (a) compute hashes → (b) generate thumbnail → (c) send to AI for analysis |
| 11.5 Progress reporting | `IProgress<LibraryScanProgress>` with current file, total count, percentage |
| 11.6 Cancellation | `CancellationToken` support so users can stop a long-running scan |
| 11.7 Background scanning | Run scan on background thread; UI remains responsive with progress bar |

---

## Phase 12 — Library Viewer UI

Build the Library tab views and view models.

| Task | Detail |
|---|---|
| 12.1 `LibraryViewModel` | Holds library data, current grouping mode, selected items, scan state |
| 12.2 `LibrarySidebarViewModel` | Folder picker (reuse `FolderNode` pattern from `FileBrowserViewModel`), group-by radio buttons, scan button + progress |
| 12.3 `LibraryDetailViewModel` | Shows info for selected image or group; face list with rename; duplicate actions |
| 12.4 `LibraryGridView.xaml` | WrapPanel/UniformGrid of thumbnail tiles inside a `ScrollViewer`; virtualized via `VirtualizingWrapPanel` or `ItemsControl` with `VirtualizingStackPanel` |
| 12.5 Group headers | When grouped by Face or Folder, show collapsible group headers with counts |
| 12.6 Thumbnail loading | Reuse existing `ThumbnailService` for generating/caching thumbnails |
| 12.7 Selection | Single and multi-select support (Ctrl+Click, Shift+Click) for bulk duplicate removal |
| 12.8 Context menu | Right-click menu: Open in Editor, Delete, Move to Folder, Assign Face |

### Grouping Modes

| Mode | Behavior |
|---|---|
| **Folder** | Group by subfolder; each group header shows folder name and image count |
| **Faces** | Group by face cluster; "Unknown" cluster for unidentified faces; images with no faces in separate group |
| **Duplicates** | Show only duplicate groups; each group shows the images + similarity score; action buttons to keep/delete |
| **Tags** | Group by AI-generated tags; an image may appear in multiple tag groups |

---

## Phase 13 — Duplicate Management Actions

| Task | Detail |
|---|---|
| 13.1 Duplicate review UI | Side-by-side comparison of duplicates within a group |
| 13.2 Auto-select keeper | Suggest keeping the highest-resolution or raw version |
| 13.3 Delete duplicates | Move to recycle bin (not permanent delete) using `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile` with `SendToRecycleBin` |
| 13.4 Bulk operations | "Remove all duplicates (keep best)" button with confirmation dialog |
| 13.5 Update library data | After deletion, remove entries from `LibraryData` and re-save JSON |

---

## Phase 14 — Edit ↔ Library Integration

| Task | Detail |
|---|---|
| 14.1 Open in Editor | Double-click or button in library → switch to Edit tab, navigate `FileBrowser` to image's folder, select the image |
| 14.2 Library from Editor | "Add to Library" or automatic: when editing an image in a scanned library folder, show its AI tags/faces in EXIF panel |
| 14.3 Shared folder state | If the library root matches the Edit tab's current folder, keep them in sync |

---

## Phase 15 — AI-Powered Auto Adjust (Stretch Goal)

Enhance the existing auto-adjust with AI scene understanding.

| Task | Detail |
|---|---|
| 15.1 Scene analysis | Before auto-adjust, send image to llama3.2-vision: "What type of photo is this? (portrait, landscape, macro, night, indoor, action, etc.)" |
| 15.2 Scene-aware presets | Map scene types to adjustment biases: portraits → warmer temperature, softer contrast, skin-tone vibrance; landscapes → cooler, more contrast, higher saturation; night → lifted shadows, reduced noise |
| 15.3 AI exposure suggestion | Ask model: "Is this image underexposed, overexposed, or well-exposed?" → bias the exposure algorithm |
| 15.4 Integration with `AutoAdjustEngine` | Add an optional `SceneHint` parameter to `Analyze()` that shifts the algorithm's targets |
| 15.5 UI toggle | Checkbox in editing panel: "🤖 AI-Assisted Auto" — when checked, Auto button queries AI first |
| 15.6 Fallback | If Ollama is unavailable, silently fall back to the existing histogram-only auto-adjust |

### Modified Auto Flow

```
User clicks "Auto":
  1. If AI-assisted enabled && Ollama available:
     a. Send thumbnail to llama3.2-vision
     b. Parse scene type + exposure assessment
     c. Map to SceneHint (preset biases)
  2. Run existing AutoAdjustEngine.Analyze() with optional SceneHint
  3. Apply results to DevelopSettings
```

---

## Implementation Order & Dependencies

```
Phase 7  (Tab View)           ── no dependencies, pure UI restructure
    │
Phase 8  (AI Service)         ── no dependencies, can develop in parallel with Phase 7
    │
Phase 9  (Duplicate Detection)── no AI dependency, uses Magick.NET only
    │
Phase 10 (Face Recognition)   ── depends on Phase 8 (AIService)
    │
Phase 11 (Library Scan)       ── depends on Phase 9 + 10 (orchestrates both)
    │
Phase 12 (Library Viewer UI)  ── depends on Phase 7 (tab structure) + Phase 11 (data)
    │
Phase 13 (Duplicate Actions)  ── depends on Phase 12 (UI) + Phase 9 (data)
    │
Phase 14 (Edit ↔ Library)     ── depends on Phase 7 + Phase 12
    │
Phase 15 (AI Auto Adjust)     ── depends on Phase 8 (stretch goal, independent)
```

### Suggested parallel work streams:

- **Stream A**: Phase 7 → Phase 12 → Phase 13 → Phase 14 (UI track)
- **Stream B**: Phase 8 → Phase 10 → Phase 15 (AI track)
- **Stream C**: Phase 9 → Phase 11 (Data/hashing track, merges into Stream A at Phase 12)

---

## File Summary — New Files to Create

| File | Type |
|---|---|
| `Models/LibraryData.cs` | Model — root library JSON object |
| `Models/LibraryImageInfo.cs` | Model — per-image analysis data |
| `Models/FaceCluster.cs` | Model — named face grouping |
| `Models/FaceInfo.cs` | Model — single detected face |
| `Models/DuplicateGroup.cs` | Model — group of duplicate images |
| `Models/ImageHash.cs` | Model — perceptual + file hashes |
| `Models/SceneHint.cs` | Model — AI scene classification (stretch) |
| `Services/AIService.cs` | Service — AI model communication |
| `Services/FaceRecognitionService.cs` | Service — face detection + clustering |
| `Services/ImageDescriptionService.cs` | Service — scene tagging via AI |
| `Services/DuplicateDetectionService.cs` | Service — perceptual/file hashing |
| `Services/LibraryScanService.cs` | Service — scan orchestrator |
| `Services/LibraryDataStore.cs` | Service — JSON persistence |
| `ViewModels/LibraryViewModel.cs` | ViewModel — library tab main VM |
| `ViewModels/LibrarySidebarViewModel.cs` | ViewModel — sidebar controls |
| `ViewModels/LibraryDetailViewModel.cs` | ViewModel — detail panel |
| `Views/LibraryView.xaml` | View — library tab content |
| `Views/LibraryView.xaml.cs` | View — code-behind |
| `Views/LibrarySidebarView.xaml` | View — sidebar panel |
| `Views/LibrarySidebarView.xaml.cs` | View — code-behind |
| `Views/LibraryGridView.xaml` | View — thumbnail grid |
| `Views/LibraryGridView.xaml.cs` | View — code-behind |
| `Views/LibraryDetailView.xaml` | View — detail panel |
| `Views/LibraryDetailView.xaml.cs` | View — code-behind |

### Files to Modify

| File | Change |
|---|---|
| `PhotoGreen.csproj` | Add OllamaSharp, Microsoft.Extensions.AI, Microsoft.Extensions.AI.Ollama package references |
| `MainWindow.xaml` | Wrap existing content in TabControl; add Library tab |
| `MainWindow.xaml.cs` | No change needed (DataContext already set) |
| `ViewModels/MainViewModel.cs` | Add `LibraryViewModel` property; add navigation commands between tabs |
| `Themes/DarkTheme.xaml` | Add styles for TabControl, thumbnail tiles, group headers |
| `Services/AutoAdjustEngine.cs` | Add `SceneHint` overload (stretch goal, Phase 15) |

---

## AI Model Prerequisites

AI models are **not bundled** with the PhotoGreen project. The app uses [Ollama](https://ollama.com) as an external local runtime that manages model downloads and inference. PhotoGreen connects to Ollama's HTTP API via the `OllamaSharp` and `Microsoft.Extensions.AI` NuGet packages.

### User Setup (One-Time)

1. **Install Ollama** — download from [ollama.com](https://ollama.com); runs as a local system service on port 11434
2. **Pull the vision model** — run `ollama pull llama3.2-vision` in a terminal (~2 GB download, stored in Ollama's model cache at `~/.ollama/models`)
3. **Launch PhotoGreen** — the `AIService` auto-connects to `http://localhost:11434`

### What Ships with PhotoGreen vs. External

| Component | Size | Included in project? | How obtained |
|---|---|---|---|
| `OllamaSharp` NuGet | ~100 KB | ✅ NuGet restore | `dotnet restore` |
| `Microsoft.Extensions.AI` NuGet | ~50 KB | ✅ NuGet restore | `dotnet restore` |
| `Microsoft.Extensions.AI.Ollama` NuGet | ~50 KB | ✅ NuGet restore | `dotnet restore` |
| Ollama runtime | ~100 MB | ❌ External install | [ollama.com](https://ollama.com) |
| `llama3.2-vision` model weights | ~2 GB | ❌ External download | `ollama pull llama3.2-vision` |

### In-App Connection Handling

- `AIService` checks Ollama availability on startup and exposes an `IsAvailable` property
- The Library sidebar shows a connection status indicator (🟢 connected / 🔴 unavailable)
- If Ollama is not running or the model is not pulled, AI features (faces, tags, description) are silently skipped
- Duplicate detection works entirely without Ollama (uses Magick.NET perceptual hashing + SHA-256)
- A "Setup AI" help link in the UI directs users to install Ollama and pull the model

---

## Key Design Decisions

1. **Local AI only** — All AI processing runs through a local Ollama instance. No cloud APIs, no data leaves the machine. Models are not embedded in the project.
2. **Graceful degradation** — Everything works without Ollama; duplicate detection is purely algorithmic; face/tag features just show "Not analyzed" if AI is unavailable.
3. **Incremental scanning** — File size + last modified date used as change detection; only new/modified images are re-analyzed. Full re-scan available as manual action.
4. **Single JSON file** — Simple, portable, human-readable. No database dependency. File is gitignore-friendly (starts with `.`).
5. **Non-destructive** — Library scanning never modifies image files. Duplicate deletion uses recycle bin.
6. **Reuse existing patterns** — Follow the same MVVM pattern with `CommunityToolkit.Mvvm`, same `ObservableObject`/`RelayCommand` style, same dark theme, same `Magick.NET` for image operations.
