# Adding or Improving a KillerPDF Translation

## File format

Each language is a single XAML `ResourceDictionary` file in the `Strings/` folder. The filename must be the BCP 47 language tag for the locale:

- `en-US.xaml` - English (US)
- `bn.xaml` - Bengali
- `cs-CZ.xaml` - Czech
- `de-DE.xaml` - German
- `es.xaml` - Spanish
- `fr-FR.xaml` - French
- `hu-HU.xaml` - Hungarian
- `it-IT.xaml` - Italian
- `ja-JP.xaml` - Japanese
- `kk-KZ.xaml` - Kazakh
- `pl-PL.xaml` - Polish
- `tr-TR.xaml` - Turkish
- `zh-CN.xaml` - Simplified Chinese
- `zh-TW.xaml` - Traditional Chinese

## How to contribute

### Editing an existing translation

1. Open the file for your language in GitHub (e.g. `Strings/zh-TW.xaml`)
2. Click the pencil icon to edit
3. Translate the text between the tags - **do not change the `x:Key` values**
4. Click "Commit changes" and open a pull request

### Adding a new language

1. Copy `Strings/en-US.xaml` and rename it to the BCP 47 tag for your language
2. Translate the values - leave the `x:Key` attributes untouched. You don't have to do all of them: any key you leave out (or delete) automatically falls back to the English text, so a partial translation is fine and can be filled in over time.
3. Open a pull request with the new file. New languages also need the maintainer to wire them into the app (the language picker and loader), so mention in your PR that it's a new locale.

## Rules

- **Never change `x:Key` values.** The app looks these up by key at runtime.
- **Keep format placeholders intact.** Some strings contain `{0}`, `{1}`, etc. - these are filled in by the app at runtime and must stay in the translation, in the same order.
- **Keep XML entities.** `&amp;` means `&`, `&#xE711;` is a glyph code - leave them as-is.
- **Missing keys fall back to English.** Every language file is layered over `en-US.xaml`, so any key you don't include just shows the English text. You never have to keep a file fully in sync with new keys - translate what you can, and untranslated bits stay readable in English.
- **Use plain hyphens (`-`), never em or en dashes (`—`, `–`),** to match the existing files, and even where your language's typography would normally call for one. This is a house style rule applied across every app and every language, so it deliberately overrides local convention. If you think it is wrong for your language, say so in the pull request rather than working around it.
- The file must be valid XML. You can check by pasting it into [xmllint.com](https://www.xmllint.com) or any XML validator.

## Format string example

```xml
<!-- English -->
<sys:String x:Key="Str_Opened">Opened {0} - {1} page(s)</sys:String>

<!-- Spanish -->
<sys:String x:Key="Str_Opened">Abierto {0} - {1} página(s)</sys:String>
```

`{0}` will be replaced with the filename and `{1}` with the page count. The placeholders must stay in the translation.

## Testing your translation

You can load your translation file into a normal KillerPDF install - no building required. Close any running KillerPDF window first (a second launch hands its arguments to the running instance), then start the app with:

```
KillerPDF.exe --lang-file C:\path\to\your\strings.xaml
```

The app runs with your file as the active language. Keys you have not translated yet show in English, exactly as a partial translation behaves in a release build.

While the app is running, every save of the file re-applies it immediately - fix a label that is too long, save, and watch it update on screen. A save with an XML error is ignored and the last good version stays applied, so you can save as often as you like. Toolbar captions and menus rebuild on the reload too.

If the file cannot be loaded at startup at all, the app tells you and starts in your normal language.

## Questions

Open a GitHub issue or leave a comment on your pull request.
