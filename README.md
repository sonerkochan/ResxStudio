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

| Shortcut | Action |
| --- | --- |
| Ctrl+O | Open |
| Ctrl+S | Save all changed files |
| Ctrl+F | Search keys and text in every language |
| Ctrl+N | Add key (prefilled with the current section) |
| Ctrl+R | Rename key in every file |
| Del (in grid) | Delete key from every file |
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

## Project layout

```
Models/ResxDocument.cs          load, apply edits, save one .resx
Models/ResourceSetLocator.cs    find the default file and all language files
ViewModels/MainViewModel.cs     filtering, stats, add/rename/delete, save
ViewModels/EntryViewModel.cs    one key with values per language (indexer binding)
MainWindow.xaml(.cs)            layout; language columns are built in code
App.xaml                        colors and control styles
```
