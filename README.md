# EasyPDF

A fast, light PDF viewer for Windows. It opens a document before its window has finished
appearing, scrolls through thousands of pages without a stutter, and stays small in memory
however long the document is. It renders with PDFium, the engine inside Google Chrome, and
follows every kind of link a PDF can hold.

## What it does

- **Opens instantly.** The document starts opening on PDFium's own thread the moment the process
  starts, in parallel with the window; the first screen is fully rendered before the window's
  first frame is shown. A second PDF opened while EasyPDF runs is handed to the running process
  and gets its own window in a few milliseconds.
- **Scrolls without waiting.** Pages are drawn in 512-pixel tiles that land exactly on the
  screen's pixels, rendered on a dedicated thread in order of what is on screen first, then what
  is ahead in the direction of reading. A tile scrolled past before it was drawn is never drawn.
  Zooming shows the existing tiles stretched at once and sharpens a moment later.
- **Stays small.** Only the tiles near the view are kept (about six screens' worth), only a few
  pages stay parsed, and nothing of the file is read until it is needed - a 2 GB file opens as
  fast as a 2 KB one. PDFium itself keeps everything it has parsed for as long as a document is
  open, which for a document of millions of objects runs past a gigabyte; once the process has
  grown 256 MB since a document was opened, its PDFium document is quietly opened afresh.
- **Huge forms.** PDFium's form module takes time that grows with the square of a form's fields
  (minutes for an EPLAN schematic's 200,000 buttons) before it draws a page. A form of more than
  5,000 fields - counted from the file while PDFium opens it, or judged from the widgets on a few
  pages when the file is encrypted or damaged - is therefore not loaded: its pages draw at once,
  its buttons still work as links, and only fields that only the form module would draw are not
  drawn.
- **Links work, all of them.** Links to places in the document (including named destinations
  and outline entries), web and mail links, web addresses that are only plain text, links to
  other PDFs - at a page or a named place inside them, from a link or an outline entry, or by
  `other.pdf#page=3` and `#nameddest=intro` - and launch actions. Buttons that go somewhere are
  links too: an EPLAN schematic's cross-references are hundreds of thousands of them. And named
  actions (Next, Previous, First and Last page, Back, Forward, Find, Print, Full screen: the
  navigation buttons of LaTeX beamer and hyperref). PDFium can read neither, so EasyPDF reads
  them from the file itself - through cross-reference and object streams, and from a copy
  PDFium writes when the file is encrypted. Hovering shows where a link goes, as a browser does.
  Where a followed link leads is marked for a moment - a red frame that blinks three times and
  fades - round the element the document itself marks (an EPLAN cross-reference ends in a
  script that names the button referring back; EasyPDF reads that call, it runs no script), the
  line of text the destination points at, the area it names, or else the whole page - and that
  element is brought into sight. Back and Forward (Alt+Left/Right, the mouse's side buttons)
  return from a jump, and the trail of jumps shows the way back (below). Links are
  hit-tested on their exact quadrilaterals, at any zoom and rotation, and a click or tap made
  before a page's links are read is still followed.
- **Safe links.** Web and mail links open directly. Other PDFs open in EasyPDF. Documents and
  other kinds of address open only after the user has seen exactly what they are. Programs,
  scripts, script addresses and everything Windows itself counts as dangerous are never run;
  device paths, alternate data streams and names Windows would quietly change are refused; and
  network paths are not touched (which would hand Windows credentials to the server) until the
  user agrees.
- **Find, select, copy.** Search highlights every match and steps through them (Enter / F3),
  starting at the page being read; match case and whole words are in the menu. Text can be
  selected by dragging (across pages too), by double-click (a word) or triple-click (a line), and
  copied.
- **Continuous, one page at a time, or a grid of pages** (the page display button, or the More
  menu): one page, two side by side, two with the cover alone (a book's spreads), two one above
  the other, 2 x 2, 3 x 2, 3 x 3 - or any grid up to 6 x 4, picked by pointing at cells. A fixed
  number at a time, nothing of the other pages is in sight. One page at a time, the arrow keys,
  Page Up/Down, Space, the wheel and the scroll bar scroll a page taller than the window, then
  turn to the next; several at a time, every key turns the whole sheet - as many pages as are
  shown - however large it is, and Ctrl+Up/Down scroll within it. A turn is at once, not
  scrolling, and each shows for a moment which pages these are ("5-8 of 2124"),
  with a line for how far through the document. A grid of several pages is fitted whole when it
  is chosen. Shift+Down/Up turns at once however far the sheet could still scroll, and, held,
  faster and faster (1, 2, 5, 10, then 25 sheets a step). Turns are smooth: the sheets either
  side are drawn ahead, so the next one is complete the moment it shows, and the one turned from
  fades out in 130 ms (not when Windows is set to show no animations, nor in quick succession). A
  touchpad's stream of small deltas turns one sheet, not a dozen. The choice is remembered for
  each document, and taken for new ones.
- **The trail of jumps.** Once a link (or the outline, or a page number typed in) has taken the
  reader somewhere, a bar over the page shows the places behind and ahead - the page the link
  was clicked on, not just the one most in view - each a click away however many steps back or
  forward it is; pointing at one shows a picture of the page and the part of the document it is
  in. A page is on the trail once: following links back and forth between two pages leaves two
  places, a link within the page being read adds none, and a page scrolled to by hand that the
  trail already has is "here", with Back going on past it. A long trail folds into "..." menus.
  The bar dims after a few seconds so the drawing under
  it shows, brightens under the pointer, and can be hidden until the next jump.
- **3D models.** A 3D annotation in PRC (EPLAN's cabinet layouts, for one) is a link: clicking
  its picture on the page shows the model over the pages, to be turned (drag, about the point
  under the pointer, the horizon kept level), moved (right-drag or Shift+drag) and zoomed toward
  the pointer (the wheel, or a pinch), from any of the views stored with it - an EPLAN model has
  one for every device - and back to the start (Home); Esc closes it. EasyPDF reads PRC itself,
  including its compressed meshes, decoded exactly, and draws it on the processor, on every core,
  as a CAD viewer does: lit faces, edge lines, see-through parts over the rest. While the model
  moves it is drawn at the resolution that keeps up, and once it is still at the screen's with
  smoothed edges. Nothing of it costs a document that is only read: a page's 3D annotations
  are found while its links are read (a microsecond a page), and the model is not touched until
  it is clicked. U3D models are not shown yet.
- **Outline and pages.** The sidebar shows the document's outline, or thumbnails of its pages
  that follow the page being read; compact rows fit half as many again into it. The scroll bar
  has a lane of its own, so it never covers the selected row.
- **Reloads by itself.** The file is never locked: whatever writes it (a LaTeX build, a report
  generator) can replace it while it is open, and EasyPDF reloads it at the same place.
- **Remembers where you were.** Reopening a document returns to the page, position, zoom,
  rotation and page display it was left at. The recent list (on the start page, and in the More
  menu with a document open) can be emptied; files pinned to it stay at its top whatever is
  opened after them, and are not cleared away. Files that are gone are marked - "Not found", or
  "Not available" for a drive or share that does not answer - from a check made in the
  background at start-up, one drive or share at a time, so a dead server holds up nothing.
- **Never waits for the network.** Nothing the window does touches a file on its own thread:
  opening a file, watching it for changes, checking whether it changed, showing it in its
  folder, handing a link to the shell and taking a dropped file all happen elsewhere, and PDFium
  is not held up either - a document on a share that does not answer just shows "Opening..."
  while every other window works on, and can be closed.
- **Prints** through Windows' print dialog: as vectors and text, as sharp as the printer goes;
  pages of documents with form fields print as 300 dpi images, so the fields' values print too.
  A print has its own copy of the document, so reloading or closing the window does not cut it
  short, and a page that cannot be printed is reported, not passed over.
- **Password-protected documents**, forms, rotated pages, mixed page sizes, damaged files (what
  can be read is shown), full screen (F11).
- **Light and dark**, following the Windows app mode live, and **English and Norwegian**,
  following the Windows display language - the same design system as Remote Desktop Manager.

## Install

Download `EasyPDF-<version>-x64.msi` from the [releases](https://github.com/bendikme/EasyPDF/releases)
and run it: it installs for the current user (no administrator rights), adds EasyPDF to the Start
menu and to "Open with" for PDF files, and starts it. Or unzip `EasyPDF-<version>-win-x64.zip`
anywhere and run `EasyPDF.exe`. Both carry their own .NET, so nothing else is needed. Settings
stay in `%LOCALAPPDATA%\EasyPDF` through updates and uninstalls. See [installer](installer/README.md).

**It keeps itself up to date.** Once a day (the first time half a minute after start-up, so it
never slows it) EasyPDF asks GitHub for the newest release. A newer one is downloaded in the
background and checked against the release's `SHA256SUMS.txt`; it then installs by itself when
EasyPDF closes - silently, nothing to click - or at once from the **Update** button that appears
in the header, which restarts EasyPDF with the same documents open. A portable copy is only told
about a newer release and pointed to its page. **Check for updates** and **Update automatically**
are in the More menu. Downloads go to `%LOCALAPPDATA%\EasyPDF\updates` and nowhere else.
`EASYPDF_UPDATES=off` switches checking off entirely.

## Requirements

- Windows 10 1809 or newer, x64
- To build: the .NET 10 SDK (and WiX 5 for the installer)

## Building

```powershell
dotnet build src\EasyPDF\EasyPDF.csproj        # development build
dotnet run --project src\EasyPDF\EasyPDF.csproj -- C:\path\to\file.pdf
.\build\publish.ps1                              # ready-to-run folder in artifacts\publish
.\build\publish.ps1 -SelfContained -Version 1.2.3
.\build\release.ps1 -Version 1.2.3               # the MSI, the portable zip and checksums in artifacts
```

A release is cut by pushing a tag: `git tag v1.2.3` then `git push origin v1.2.3`. The release
workflow (`.github/workflows/release.yml`) builds the installer and the zip and publishes them
as a GitHub release.

## Checks

```powershell
dotnet run --project tests\EasyPDF.Tests        # all regression checks; exits nonzero on a failure
dotnet run --project tests\EasyPDF.Tests -- links   # only the checks whose name contains "links"
dotnet run --project tests\EasyPDF.Tests -c Release -- --bench [file.pdf]
```

The checks open real documents (`tests\fixtures`, made by `tests\fixtures\make_fixtures.py`) in
real, off-screen windows and drive them the way a user does. They cover every link kind on
every kind of page (rotated, zoomed, scrolled, before its links are read), one page at a time
and grids by key, wheel, touchpad, scroll bar and picker, fast and smooth turns, the trail of
jumps, the mark on a link's destination (EPLAN's, on turned pages too), compact rows, pinned and missing
recent files, a share that does not answer (played by a stand-in that stalls), named actions and
buttons in plain, compressed and encrypted files, a form of 20,000 buttons, a document opened
afresh to free PDFium's memory, the link policy, tiles that must join the whole-page
render pixel for pixel at every rotation and zoom, search, selection, outline, passwords, 3D
annotations (found among a page's links, their views read, models that cannot be shown saying
so) and the 3D view's renderer and camera (lighting, edge lines, see-through parts without
seams, what is behind the camera, turning, zooming, going between views),
damaged and missing files, files past 2 GB and 4 GB, reload on change, memory staying within
its budget, updating (against a stand-in for GitHub: a newer release downloaded, checked and installed on
closing or by a restart that reopens the documents, a bad checksum or size refused, a portable
copy only pointed to the release, nothing taken from anywhere but GitHub),
closed windows and documents being released, managed memory that stays flat (within a few kilobytes) over
eighteen rounds of opening and closing seven kinds of document - continuous, one page at a time
and in a 2 x 2 grid - printing into a device context, every
dialog and menu in both languages, and the real executable handing a second launch over to the
running one. The reader of the file's own objects is fed thousands of damaged files from memory that
ends at an inaccessible page, so a single byte read too far would stop the run.
`--diag-file <file.pdf>` and `--diag-memory <file.pdf> [--all]` time a document's opening,
first tiles and links, and follow memory as its pages are read. `--prc <file.pdf> [page]
[--png out.png] [--bench]` checks that every compressed mesh of a document's 3D models decodes
exactly, draws the first from its first view and times frames of it; `--model-cost <file.pdf>`
times what finding 3D annotations adds to reading each page's links. Nothing reaches
the shell, the user's settings or a running EasyPDF - the last check makes sure of it.

`tests\tools` turns the screenshot tool's output into PNGs (`--screenshot <folder> [large.pdf]`, then
`python tests\tools\xps2png.py <folder>`) and rebuilds the icon (`make_icon.py`).

Set `EASYPDF_TRACE` to a file path to have the app record its start-up milestones there
(and `EASYPDF_TRACE_EXIT=1` to close once the first screen is rendered); `EASYPDF_DATA` keeps
settings and the log in another folder.

## Keys

| | |
| --- | --- |
| Ctrl+O, Ctrl+N, Ctrl+W | Open, new window, close window |
| Ctrl+F, Enter / F3, Shift+Enter / Shift+F3, Esc | Find, next match, previous match, clear |
| Ctrl+G | Go to page |
| Alt+Left, Alt+Right | Back, forward |
| Ctrl++, Ctrl+-, Ctrl+wheel, pinch | Zoom in and out |
| Ctrl+0, Ctrl+1, Ctrl+2 | Fit page, actual size, fit width |
| Ctrl+Shift++, Ctrl+Shift+- | Rotate clockwise, counterclockwise |
| Ctrl+C, Ctrl+A | Copy, select all |
| Ctrl+P, Ctrl+D | Print, document properties |
| F4, F5, F11 | Sidebar, reload, full screen |
| Page Up/Down, Space, Home, End, arrows | Move through the document; several pages at a time, each turns the whole sheet |
| Ctrl+Up, Ctrl+Down | Several pages at a time: scroll within a sheet taller than the window |
| Shift+Down, Shift+Up | Turn at once; held, faster and faster |
| In a 3D model: arrows, +, -, Home, Esc | Turn, zoom in and out, the start view, back to the pages |

## How it is put together

| Folder | What is in it |
| --- | --- |
| `src\EasyPDF\Interop` | PDFium's C API, every signature blittable |
| `src\EasyPDF\Pdf` | The engine thread every PDFium call runs on, the document (pages, links, text, outline, search, printing), the page canvas that makes tiles seamless, file access that never locks, and a reader of the file's own objects for what PDFium cannot tell (a form's size, what buttons and named actions do) |
| `src\EasyPDF\Prc` | A reader of PRC, the 3D format of PDF 3D annotations: its bit stream, the product structure, styles, and the compressed meshes |
| `src\EasyPDF\Viewer` | The page view: layout in device pixels, tile planning and rendering, scrolling, zoom, links, selection, search; and the 3D view: the model placed, its camera, and the renderer |
| `src\EasyPDF\Views` | The window and dialogs; `Themes` holds the shared light/dark design system |
| `src\EasyPDF\Services` | Settings, link policy, printing, single instance, theme, language, log |
| `src\EasyPDF\Resources` | Every piece of UI text: `Strings.resx` (English) and `Strings.nb.resx` (Norwegian). A new string goes into both |
| `tests` | The regression checks, their fixtures and tools |
| `installer`, `build` | The per-user MSI (WiX 5), and the publish and release scripts |

Third-party notices are in `src\EasyPDF\ThirdParty` and ship beside the executable in
`licenses\`.
