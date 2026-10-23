# ResxStudio

A small WPF editor for .resx translation files. Open the default file
(`Translations.resx`) and every language file next to it opens side by side.

## Build and run

Requires Windows and the .NET 8 SDK.

```
dotnet run
```

or open `ResxStudio.csproj` in Visual Studio and press F5. To get a single exe:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

You can also pass a file on the command line (`ResxStudio.exe path\to\Translations.resx`),
and the app reopens the last file you used.

## How it works

- **Sections** on the left are the key prefixes (`Home`, `Manager`, `Admin`...).
  The rose number is how many keys in that section still have missing text.
- **Language chips** at the top show progress per language. Click one to hide or show
  its column; with 10 languages, hide the ones you aren't working on. "Untranslated
  only" and F8 only look at the visible languages.
- **The editor panel** on the right shows the selected key with a full-size box per
  language. Edits there and in the grid are the same data.
- **Rose** means missing text. **Amber keys** exist in a translation but not in the
  default file, so there's no fallback for them.

## Autofill missing translations (Ctrl+T)

Fills empty cells using the free MyMemory API. Pick the languages and which keys
(current view, all, or the selected one), then press Start.

- **Free limits:** 5,000 characters a day, or 50,000 if you enter an email address.
  The window estimates how many days a job takes. When the limit runs out, everything
  translated so far is kept; run it again the next day and it continues with what's left.
- **Protected tokens:** `{0}` style placeholders, `{name}` / `{{name}}` / `${name}`,
  HTML tags and entities, Razor (`@Model.Name`, `@(...)`), printf (`%s`, `%1$d`), URLs,
  escapes like `\n`, and names from the "Never translate" list are swapped for markers
  before sending and put back afterwards. If the service damages a marker, only the text
  between the tokens is translated so the tokens are copied over untouched.
- **Line breaks and outer spaces** are kept; long texts are split by sentence to fit the
  500-byte request limit. Identical texts are translated once.
- **Review:** filled cells turn amber and show "Auto-translated". Editing a cell or
  clicking "Mark reviewed" clears it. Use the "Auto-translated, to review" filter to go
  through them. The marks are remembered between sessions (in %AppData%, not in your files).
- Text is sent to MyMemory (translated.net), a public service. Nothing is written to
  your .resx files until you save.

## Problems panel

Click **Problems** in the toolbar. Grouped by type, click a row to jump to that cell:

| Type | Severity |
| --- | --- |
| Lost or changed placeholders (`{0}`, tags, Razor...) compared with the default text | Error / warning |
| Malformed format strings (unbalanced braces, `{x}` in a `{0}`-style string) | Error |
| Missing from the default file (no fallback) | Error |
| Duplicate keys in a file, and keys differing only by case | Warning |
| Different leading or trailing spacing | Note |
| Same as the default text | Note |
| Missing translations | Note |

The editor panel shows the same checks under each box as you type. Saving with broken or
malformed placeholders asks first, because `string.Format` throws on them at runtime.

## Changes made outside the editor

ResxStudio remembers a hash of each file when it loads or saves it.

- When you switch back to the window, a banner tells you if a file changed on disk
  (git pull, Visual Studio, another editor) with a Reload button.
- Before saving, changed files are detected and you choose:
  **Merge** keeps the other changes and applies only the keys you edited (if both sides
  changed the same key, yours wins and you get a list), or **Overwrite**.

| Shortcut | Action |
| --- | --- |
| Ctrl+O | Open |
| Ctrl+S | Save all changed files |
| Ctrl+F | Search keys and text in every language |
| Ctrl+N | Add key (prefilled with the current section) |
| Ctrl+R | Rename key in every file |
| Del (in grid) | Delete key from every file |
| Ctrl+T | Autofill missing translations |
| F8 | Next untranslated key, cursor in the empty box |
| F5 | Reload from disk |

## What saving does

- Only files with changes are written, so git diffs stay small.
- The XML is edited in place: the header, schema, comments, non-string resources and
  key order are kept. New keys are inserted alphabetically.
- Files are written with 2-space indentation and CRLF like Visual Studio, keeping the
  original BOM setting, via a temp file so a failed save never leaves a half-written file.
- An empty translation is removed rather than saved, because an empty value would hide
  the default text at runtime.
- Both naming styles work: `Translations.bg.resx` and `Translations_bg.resx`. New
  languages use whichever style your files already use.

If your project has a `Translations.Designer.cs`, Visual Studio regenerates it only when
the .resx changes inside VS. After adding or renaming keys here, right-click the .resx
and choose **Run Custom Tool**.

## If something goes wrong

- Everything the app does at startup is written to `%AppData%\ResxStudio\log.txt`.
  A watchdog adds a `FREEZE:` line with the last step if the window stops responding
  for 8 seconds. Send that file along with any bug report.
- Hold **Shift** while starting the app to skip reopening the last file.
- If opening a file crashed or froze last time, it isn't reopened automatically.
- Files are read on a background thread with a loading screen, so the window stays
  responsive while a large set opens.

## Project layout

```
Models/ResxDocument.cs          load, apply edits, save one .resx
Models/ResourceSetLocator.cs    find the default file and all language files
ViewModels/MainViewModel.cs     filtering, stats, add/rename/delete, save
ViewModels/EntryViewModel.cs    one key with values per language (indexer binding)
Models/ResxMerger.cs            key-level merge when a file changed on disk
Services/TokenProtector.cs      find, mask and restore placeholders and names
Services/TranslationChecks.cs   placeholder, format string and spacing checks
Services/MyMemoryClient.cs      MyMemory API calls, quota detection
Services/MachineTranslator.cs   line, chunk and fallback handling around the API
Views/AutofillDialog.xaml(.cs)  autofill window
MainWindow.xaml(.cs)            layout; language columns are built in code
App.xaml                        colors and control styles
```
