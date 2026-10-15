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
  fades - round the line of text the destination points at, the area it names, or the whole page.
  Back and Forward (Alt+Left/Right, the mouse's side buttons) return from a jump. Links are
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
- **One page at a time**, or one continuous column (the zoom menu or the More menu). One page
  at a time, nothing of the other pages is in sight; the arrow keys, Page Up/Down, Space, the
  wheel and the scroll bar scroll a page taller than the window, then turn to the next - at once,
  not scrolling - and each turn shows for a moment which page it is, of how many, with a line
  for how far through the document. A touchpad's stream of small deltas turns one page, not a
  dozen. The choice is remembered for each document, and taken for new ones.
- **Outline and pages.** The sidebar shows the document's outline, or thumbnails of its pages
  that follow the page being read.
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

## Requirements

- Windows 10 1809 or newer, x64
- The .NET 10 Desktop Runtime (or publish self-contained, which needs nothing)

## Building

```powershell
dotnet build src\EasyPDF\EasyPDF.csproj        # development build
dotnet run --project src\EasyPDF\EasyPDF.csproj -- C:\path\to\file.pdf
.\build\publish.ps1                              # ready-to-run folder in artifacts\publish
.\build\publish.ps1 -SelfContained -Version 1.2.3
```

## Checks

```powershell
dotnet run --project tests\EasyPDF.Tests        # all regression checks; exits nonzero on a failure
dotnet run --project tests\EasyPDF.Tests -- links   # only the checks whose name contains "links"
dotnet run --project tests\EasyPDF.Tests -c Release -- --bench [file.pdf]
```

The checks open real documents (`tests\fixtures`, made by `tests\fixtures\make_fixtures.py`) in
real, off-screen windows and drive them the way a user does. They cover every link kind on
every kind of page (rotated, zoomed, scrolled, before its links are read), one page at a time
by key, wheel, touchpad and scroll bar, the mark on a link's destination, pinned and missing
recent files, a share that does not answer (played by a stand-in that stalls), named actions and
buttons in plain, compressed and encrypted files, a form of 20,000 buttons, a document opened
afresh to free PDFium's memory, the link policy, tiles that must join the whole-page
render pixel for pixel at every rotation and zoom, search, selection, outline, passwords,
damaged and missing files, files past 2 GB and 4 GB, reload on change, memory staying within
its budget, closed windows and documents being released, managed memory that stays flat (within a few kilobytes) over
sixteen rounds of opening and closing seven kinds of document, printing into a device context, every
dialog and menu in both languages, and the real executable handing a second launch over to the
running one. The reader of the file's own objects is fed thousands of damaged files from memory that
ends at an inaccessible page, so a single byte read too far would stop the run.
`--diag-file <file.pdf>` and `--diag-memory <file.pdf> [--all]` time a document's opening,
first tiles and links, and follow memory as its pages are read. Nothing reaches
the shell, the user's settings or a running EasyPDF - the last check makes sure of it.

`tests\tools` turns the screenshot tool's output into PNGs (`--screenshot <folder>`, then
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
| Page Up/Down, Space, Home, End, arrows | Move through the document |

## How it is put together

| Folder | What is in it |
| --- | --- |
| `src\EasyPDF\Interop` | PDFium's C API, every signature blittable |
| `src\EasyPDF\Pdf` | The engine thread every PDFium call runs on, the document (pages, links, text, outline, search, printing), the page canvas that makes tiles seamless, file access that never locks, and a reader of the file's own objects for what PDFium cannot tell (a form's size, what buttons and named actions do) |
| `src\EasyPDF\Viewer` | The page view: layout in device pixels, tile planning and rendering, scrolling, zoom, links, selection, search |
| `src\EasyPDF\Views` | The window and dialogs; `Themes` holds the shared light/dark design system |
| `src\EasyPDF\Services` | Settings, link policy, printing, single instance, theme, language, log |
| `src\EasyPDF\Resources` | Every piece of UI text: `Strings.resx` (English) and `Strings.nb.resx` (Norwegian). A new string goes into both |
| `tests` | The regression checks, their fixtures and tools |

Third-party notices are in `src\EasyPDF\ThirdParty` and ship beside the executable in
`licenses\`.
