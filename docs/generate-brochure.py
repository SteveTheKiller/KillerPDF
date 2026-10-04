"""Build the KillerPDF 1.8 brochure from the current app resources.

Run with the bundled Codex Python or any Python with reportlab installed.
The shortcut reference, locale count, and theme samples come from this checkout.
"""

from __future__ import annotations

import html
import re
from pathlib import Path
from xml.etree import ElementTree

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas
from reportlab.platypus import Paragraph


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "KillerPDF.pdf"
W, H = A4
INK = colors.HexColor("#17231d")
MUTED = colors.HexColor("#53635a")
PAPER = colors.HexColor("#f7f5ec")
GREEN = colors.HexColor("#2ec768")
YELLOW = colors.HexColor("#ffe44e")
LINE = colors.HexColor("#d8ded4")
FONT_DIR = Path("C:/Windows/Fonts")
pdfmetrics.registerFont(TTFont("Body", str(FONT_DIR / "DejaVuSans.ttf")))
pdfmetrics.registerFont(TTFont("Bold", str(FONT_DIR / "DejaVuSans-Bold.ttf")))
pdfmetrics.registerFont(TTFont("Mono", str(FONT_DIR / "DejaVuSansMono.ttf")))

BODY = ParagraphStyle("body", fontName="Body", fontSize=9.4, leading=15.4, textColor=INK)
SMALL = ParagraphStyle("small", parent=BODY, fontSize=8, leading=12.3)
WHITE = ParagraphStyle("white", parent=BODY, textColor=colors.white)
MARGIN = 48


def para(c, text, x, top, width, style=BODY):
    p = Paragraph(text, style)
    _, height = p.wrap(width, H)
    p.drawOn(c, x, top - height)
    return top - height


def rounded(c, x, y, w, h, fill, radius=13, stroke=None):
    c.setFillColor(fill)
    c.setStrokeColor(stroke or fill)
    c.roundRect(x, y, w, h, radius, stroke=int(stroke is not None), fill=1)


def gradient(c, x, y, w, h, top, bottom, steps=44):
    a = colors.toColor(top)
    b = colors.toColor(bottom)
    for i in range(steps):
        t = i / max(steps - 1, 1)
        c.setFillColor(colors.Color(a.red * (1 - t) + b.red * t,
                                     a.green * (1 - t) + b.green * t,
                                     a.blue * (1 - t) + b.blue * t))
        c.rect(x, y + h * i / steps, w, h / steps + 0.25, stroke=0, fill=1)


def chrome(c, number, section):
    c.setFillColor(PAPER)
    c.rect(0, 0, W, H, stroke=0, fill=1)
    c.setFillColor(INK)
    c.setFont("Bold", 9)
    c.drawString(MARGIN, H - 36, "Killer")
    c.setFillColor(GREEN)
    c.drawString(MARGIN + 29, H - 36, "PDF")
    c.setStrokeColor(LINE)
    c.line(MARGIN, H - 47, W - MARGIN, H - 47)
    c.setFillColor(MUTED)
    c.setFont("Body", 7.5)
    c.drawRightString(W - MARGIN, H - 36, section.upper())
    c.line(MARGIN, 39, W - MARGIN, 39)
    c.drawString(MARGIN, 25, "KillerPDF 1.8  |  Technical and feature guide")
    c.drawRightString(W - MARGIN, 25, f"{number:02d}")


def title(c, kicker, heading, desc=None):
    c.setFillColor(GREEN)
    c.setFont("Bold", 9)
    c.drawString(MARGIN, H - 84, kicker.upper())
    c.setFillColor(INK)
    c.setFont("Bold", 24)
    c.drawString(MARGIN, H - 117, heading)
    y = H - 137
    if desc:
        y = para(c, desc, MARGIN, y, W - 2 * MARGIN, SMALL) - 15
    return y


def section(c, text, x, y):
    c.setFillColor(INK)
    c.setFont("Bold", 11)
    c.drawString(x, y, text)
    return y - 17


def feature(c, x, y, w, label, body, accent=GREEN, h=105):
    rounded(c, x, y - h, w, h, colors.white, 11, LINE)
    c.setFillColor(accent)
    c.roundRect(x + 13, y - 25, 24, 5, 2, stroke=0, fill=1)
    c.setFillColor(INK)
    c.setFont("Bold", 10)
    c.drawString(x + 13, y - 45, label)
    para(c, body, x + 13, y - 55, w - 26, SMALL)


def page(c, number, name, kicker, heading, desc=None):
    chrome(c, number, name)
    return title(c, kicker, heading, desc)


def get_shortcuts():
    source = (ROOT / "Services" / "ShortcutTable.cs").read_text(encoding="utf-8")
    english = (ROOT / "Strings" / "en-US.xaml").read_text(encoding="utf-8")
    labels = dict(re.findall(r'x:Key="([^"]+)">([^<]*)', english))
    rows = []
    for keys, key, cat in re.findall(r'B\("([^"]+)",\s*"([^"]+)",\s*"([^"]+)"', source):
        if cat not in {"File", "Tools", "Edit", "Help", "Nav", "View", "Ocr", "Search"}:
            continue
        pretty = keys
        for a, b in {"%ctrl%": "Ctrl", "%shift%": "Shift", "%alt%": "Alt",
                     "%or%": "or", "%del%": "Delete", "%enter%": "Enter",
                     "%esc%": "Esc", "%menu%": "Menu", "%pgup%": "PgUp",
                     "%pgdn%": "PgDn", "%scroll%": "Scroll", "%zin%": "=",
                     "%zout%": "-", "%middledrag%": "Middle drag",
                     "%spacedrag%": "Space + drag", "%wheellogo%": "Wheel on logo",
                     "%tab%": "Tab", "%click%": "Click"}.items():
            pretty = pretty.replace(a, b)
        pretty = pretty.replace("←", "Left").replace("→", "Right")
        pretty = pretty.replace("↑", "Up").replace("↓", "Down")
        label = "Bold, italic, underline" if key == "Str_KS_TextStyle" else html.unescape(labels.get(key, key))
        rows.append((cat, pretty, label))
    return rows


def shortcut_column(c, rows, x, top, width):
    y = top
    last = None
    for cat, keys, label in rows:
        if cat != last:
            if last is not None:
                y -= 12
            c.setFillColor(GREEN)
            c.setFont("Bold", 8)
            c.drawString(x, y, cat.upper())
            y -= 17
            last = cat
        c.setFillColor(INK)
        c.setFont("Mono", 7.2)
        c.drawString(x, y, keys[:37])
        c.setFillColor(MUTED)
        c.setFont("Body", 7.2)
        c.drawRightString(x + width, y, label[:35])
        c.setStrokeColor(LINE)
        c.line(x, y - 4, x + width, y - 4)
        y -= 16
    return y


def theme_data(name):
    root = ElementTree.parse(ROOT / "Themes" / f"{name}.xaml").getroot()
    found = {}
    key = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"
    for el in root.iter():
        k = el.get(key)
        if k in {"BackgroundBrush", "SurfaceBrush", "PrimaryBrush", "SelectionBg"}:
            stops = [x.get("Color") for x in el if x.get("Color")]
            found[k] = stops or [el.get("Color", "#333333")]
    return found


def swatch(c, x, y, w, h, name):
    d = theme_data(name)
    bg = d.get("BackgroundBrush", ["#222222"])
    surface = d.get("SurfaceBrush", ["#333333"])[0]
    accent = d.get("PrimaryBrush", ["#2ec768"])[0]
    selected = d.get("SelectionBg", [accent])
    if len(bg) > 1:
        gradient(c, x, y, w, h, bg[-1], bg[0])
    else:
        c.setFillColor(colors.HexColor(bg[0]))
        c.rect(x, y, w, h, stroke=0, fill=1)
    rounded(c, x + 9, y + 12, w - 18, h - 27, colors.HexColor(surface), 5)
    if len(selected) > 1:
        gradient(c, x + 16, y + 19, w - 32, 13, selected[-1], selected[0])
    else:
        c.setFillColor(colors.HexColor(selected[0]))
        c.rect(x + 16, y + 19, w - 32, 13, fill=1, stroke=0)
    c.setFillColor(colors.HexColor(accent))
    c.circle(x + w - 21, y + h - 27, 6, fill=1, stroke=0)
    c.setFillColor(INK)
    c.setFont("Bold", 8.2)
    c.drawString(x, y - 14, name)


def build():
    c = canvas.Canvas(str(OUTPUT), pagesize=A4, pageCompression=1)
    c.setTitle("KillerPDF 1.8 | Technical and feature guide")
    c.setAuthor("Steve the Killer")
    c.setSubject("Features, themes, languages, keyboard shortcuts, and PDF workflows")

    # 1: Cover.
    gradient(c, 0, 0, W, H, "#1b2a23", "#0c1310", 84)
    c.drawImage(str(ROOT / "pdf-landing" / "kp-icon.png"), MARGIN, H - 150,
                width=63, height=63, mask="auto")
    c.setFont("Bold", 40)
    c.setFillColor(colors.white)
    c.drawString(MARGIN, H - 236, "Killer")
    c.setFillColor(GREEN)
    c.drawString(MARGIN + 119, H - 236, "PDF")
    c.setFillColor(colors.HexColor("#d7e6d9"))
    c.setFont("Body", 15)
    c.drawString(MARGIN, H - 271, "A practical guide to the 1.8 family")
    c.setStrokeColor(colors.HexColor("#456951"))
    c.line(MARGIN, H - 291, W - MARGIN, H - 291)
    para(c, "Read, annotate, edit, OCR, sign, print, and save PDFs locally on Windows. "
         "This guide documents the current 1.8 application, its shortcuts, all 19 interface "
         "languages, and all 13 themes.", MARGIN, H - 322, W - 2 * MARGIN, WHITE)
    for i, (big, small) in enumerate([("19", "interface locales"), ("13", "themes"),
                                       ("71", "shortcut rows")]):
        x = MARGIN + i * 166
        rounded(c, x, 118, 144, 80, colors.HexColor("#24392b"), 10)
        c.setFillColor(YELLOW if i == 1 else GREEN)
        c.setFont("Bold", 27)
        c.drawString(x + 14, 154, big)
        c.setFillColor(colors.white)
        c.setFont("Body", 8)
        c.drawString(x + 14, 137, small)
    c.setFillColor(colors.HexColor("#b9c8bc"))
    c.setFont("Body", 8)
    c.drawString(MARGIN, 54, "KillerPDF 1.8.80  |  October 2026")
    c.showPage()

    # 2: Overview.
    y = page(c, 2, "Overview", "The application", "PDF work, in one place",
             "A native Windows application with local processing and an independent PDF document engine.")
    cards = [
        ("Read", "Single, Continuous, Two Page, Grid, and Book layouts. Tabs, thumbnails, bookmarks, split pane, and jump history."),
        ("Edit", "Text boxes, covers, highlights, ink, shapes, images, signatures, stamps, links, and fillable form fields."),
        ("Convert", "Merge, split, extract, crop, transform, flatten, render to images, and produce searchable scanned PDFs."),
        ("Deliver", "Print preview, page ranges, N-up layout, digital signatures, and save workflows with document structure checks."),
    ]
    for i, (head, body) in enumerate(cards):
        x = MARGIN + (i % 2) * 250
        yy = y - (i // 2) * 132
        feature(c, x, yy, 232, head, body, YELLOW if i == 2 else GREEN, 117)
    y -= 294
    y = section(c, "The local workflow", MARGIN, y)
    para(c, "Your document is opened, displayed, and edited on your computer. OCR uses "
         "downloadable Tesseract models. The installed build uses the .NET 10 Desktop Runtime; "
         "the portable package includes its runtime.", MARGIN, y, W - 2 * MARGIN)
    c.showPage()

    # 3: Reading.
    y = page(c, 3, "Reading", "Explore", "Read with control",
             "The same document can be viewed several ways without changing the PDF itself.")
    feature(c, MARGIN, y, 232, "View modes",
            "Continuous for long reading, Single Page for focus, Two Page and Book for spreads, Grid for an overview.", h=125)
    feature(c, MARGIN + 250, y, 232, "Navigation",
            "Use page keys, thumbnails, bookmarks, tabs, jump history, and a second pane to compare distant pages.", h=125)
    y -= 162
    y = section(c, "Zoom and rendering", MARGIN, y)
    y = para(c, "Fit Page, Fit Width, Actual Size, and cursor-anchored zoom keep the current area in view. "
             "The page image and the interactive annotation layer are kept separate on screen.", MARGIN, y,
             W - 2 * MARGIN) - 35
    y = section(c, "Search and selection", MARGIN, y)
    para(c, "Find text across a document, move between results, select page text, and copy what you need. "
         "For image-only scans, Make Searchable PDF adds a text layer with OCR.", MARGIN, y, W - 2 * MARGIN)
    c.showPage()

    # 4: Editing.
    y = page(c, 4, "Editing", "Create", "Annotate and revise",
             "Marks remain editable in the live session and are committed through the save workflow.")
    items = [
        ("Text and covers", "Add text, or replace existing words using a linked cover and text box."),
        ("Highlights and ink", "Mark passages, draw freehand, add lines and shapes, then adjust color and opacity."),
        ("Images and signatures", "Place pictures and signatures, move and resize them, or create page stamps."),
        ("Pages and links", "Crop, rotate, reorder, split, merge, extract, and add links."),
    ]
    for i, (head, body) in enumerate(items):
        feature(c, MARGIN + (i % 2) * 250, y - (i // 2) * 132, 232, head, body, h=118)
    c.showPage()

    # 5: Forms and OCR.
    y = page(c, 5, "Forms and OCR", "Work with scans", "Forms that save. Scans that search.")
    y = section(c, "Interactive fields", MARGIN, y)
    y = para(c, "Fill existing PDF form fields, add a fillable text field with F, and save the "
             "completed document. The engine preserves form structure while writing.", MARGIN, y,
             W - 2 * MARGIN) - 26
    y = section(c, "On-device OCR", MARGIN, y)
    y = para(c, "OCR Page copies text from the current page. OCR Region reads a selected area. "
             "Make Searchable PDF adds an invisible text layer to a scanned document. "
             "Models download on first use and remain cached.", MARGIN, y, W - 2 * MARGIN) - 26
    y = section(c, "Model choices", MARGIN, y)
    para(c, "The OCR catalog has 19 language models, including Norwegian, Brazilian Portuguese, "
         "Ukrainian, and Vietnamese. Standard and High Quality downloads are available.",
         MARGIN, y, W - 2 * MARGIN)
    c.showPage()

    # 6: Output.
    y = page(c, 6, "Output", "Finish", "Save, print, and share")
    for i, (head, body) in enumerate([
        ("Save", "Write a new copy with edits while checking the document structure. Keep the original if recovery is required."),
        ("Print", "Preview page ranges, printer settings, scaling, alignment, color, copies, duplex, and N-up layouts."),
        ("Export", "Extract pages, split documents, flatten a copy, or render pages as PNG and JPEG images."),
        ("Automate", "Use the command line for merging, extraction, OCR, rendering, printing, and batch resave."),
    ]):
        feature(c, MARGIN + (i % 2) * 250, y - (i // 2) * 132, 232, head, body, h=118)
    c.showPage()

    # 7: Engine.
    y = page(c, 7, "Engine", "Under the hood", "A separate PDF engine",
             "The KillerPDF.Engine is the UI-free .NET library used by the desktop application.")
    feature(c, MARGIN, y, 232, "Document structure",
            "Parsing, object graphs, page trees, forms, annotations, metadata, and cross references.", h=120)
    feature(c, MARGIN + 250, y, 232, "Writing",
            "Incremental updates and full rewrites, with validation before output.", h=120)
    y -= 158
    feature(c, MARGIN, y, 232, "Security",
            "Encryption, permission checks, decryption, signing, and signature verification.", h=120)
    feature(c, MARGIN + 250, y, 232, "Rendering",
            "PDFium draws pages for viewing, print preview, image export, and OCR input.", h=120)
    y -= 157
    y = section(c, "Validation", MARGIN, y)
    para(c, "KillerPDF checks saved PDFs with structural and standards-oriented tooling. "
         "Validation results describe the specific version and corpus tested; they are "
         "not a guarantee that every possible PDF is supported.", MARGIN, y, W - 2 * MARGIN)
    c.showPage()

    # 8 and 9: Live shortcut reference.
    rows = get_shortcuts()
    for number, left_cats, right_cats, label in [
        (8, {"File", "Tools"}, {"Edit", "Help"}, "File, tools, and editing"),
        (9, {"Nav", "Ocr"}, {"View", "Search"}, "Navigation and view"),
    ]:
        y = page(c, number, "Shortcuts", "Keyboard reference", label,
                 "Generated from the same shortcut table used by the in-app list and keyboard map.")
        shortcut_column(c, [r for r in rows if r[0] in left_cats], MARGIN, y - 5, 230)
        shortcut_column(c, [r for r in rows if r[0] in right_cats], MARGIN + 252, y - 5, 230)
        c.showPage()

    # 10: Themes.
    y = page(c, 10, "Themes", "Appearance", "Thirteen themes",
             "Choose a look without restarting. The cards below sample the theme resources in this checkout.")
    names = [p.stem for p in (ROOT / "Themes").glob("*.xaml")]
    names = [n for n in names if n in {"Dark", "Light", "Black", "98SE", "Blood", "Greed",
                                       "Cyanotic", "Ectoplasm", "Decay", "Malaise",
                                       "Sepulchre", "Delirium", "Mourning"}]
    names.sort(key=lambda n: ["Dark", "Light", "Black", "98SE", "Blood", "Greed",
                              "Cyanotic", "Ectoplasm", "Decay", "Malaise",
                              "Sepulchre", "Delirium", "Mourning"].index(n))
    for i, name in enumerate(names):
        col, row = i % 4, i // 4
        swatch(c, MARGIN + col * 125, y - 106 - row * 127, 109, 76, name)
    c.showPage()

    # 11: Accents and gradients.
    y = page(c, 11, "Accents", "Appearance", "One family, many moods",
             "Dark, Light, Black, and 98SE each accept eight accent colors: 41 combinations in total.")
    accent_names = ["Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Purple", "Magenta"]
    accent_colors = ["#ed4b50", "#f69624", "#ffe44e", "#2ec768", "#23aaa1", "#318eda", "#8d4bd1", "#ee42b3"]
    for i, (name, color) in enumerate(zip(accent_names, accent_colors)):
        xx = MARGIN + (i % 4) * 125
        yy = y - 130 - (i // 4) * 135
        rounded(c, xx, yy, 109, 76, colors.white, 8, LINE)
        gradient(c, xx + 9, yy + 11, 91, 30, color, "#fff8ce" if name == "Yellow" else "#eff9f2")
        c.setFillColor(INK)
        c.setFont("Bold", 8)
        c.drawString(xx, yy - 15, name)
    para(c, "Selected surfaces use subtle gradients across the modern theme family. "
         "The 98SE base keeps its classic flat selection treatment. "
         "Sunflower yellow uses a bright gradient and dark text for contrast.",
         MARGIN, y - 375, W - 2 * MARGIN)
    c.showPage()

    # 12: Languages.
    y = page(c, 12, "Languages", "Localized", "Nineteen interface locales",
             "The language picker changes the desktop interface immediately.")
    lang = ["English", "Bengali", "Czech", "German", "Spanish", "French", "Hungarian",
            "Italian", "Japanese", "Kazakh", "Norwegian Bokmål", "Polish",
            "Brazilian Portuguese", "Russian", "Turkish", "Ukrainian",
            "Vietnamese", "Chinese (Simplified)", "Chinese (Traditional)"]
    for i, label in enumerate(lang):
        col, row = i % 2, i // 2
        xx = MARGIN + col * 250
        yy = y - 35 - row * 42
        rounded(c, xx, yy - 24, 231, 31, colors.white, 6, LINE)
        c.setFillColor(GREEN if label in {"Norwegian Bokmål", "Brazilian Portuguese",
                                         "Ukrainian"} else MUTED)
        c.circle(xx + 14, yy - 8, 3, fill=1, stroke=0)
        c.setFillColor(INK)
        c.setFont("Body", 8.8)
        c.drawString(xx + 26, yy - 11, label)
    para(c, "New in this cycle: Norwegian Bokmål, Brazilian Portuguese, and Ukrainian.",
         MARGIN, 151, W - 2 * MARGIN, SMALL)
    c.showPage()

    # 13: CLI.
    y = page(c, 13, "Automation", "Command line", "The app can work headlessly",
             "Use the portable executable or the installed application from PowerShell or cmd.")
    examples = [
        ("Merge", "KillerPDF.exe --merge out.pdf a.pdf b.pdf"),
        ("Extract", "KillerPDF.exe --extract-pages in.pdf 1-3,5 out.pdf"),
        ("OCR", "KillerPDF.exe --ocr scan.pdf searchable.pdf --lang eng"),
        ("Render", "KillerPDF.exe --to-image in.pdf images\\ --dpi 300"),
        ("Help", "KillerPDF.exe --help"),
    ]
    for i, (name, command) in enumerate(examples):
        yy = y - i * 74
        rounded(c, MARGIN, yy - 57, W - 2 * MARGIN, 62, colors.white, 8, LINE)
        c.setFillColor(GREEN)
        c.setFont("Bold", 8)
        c.drawString(MARGIN + 14, yy - 18, name.upper())
        c.setFillColor(INK)
        c.setFont("Mono", 8)
        c.drawString(MARGIN + 14, yy - 39, command)
    c.showPage()

    # 14: Interactive test page.
    y = page(c, 14, "Try the PDF", "Interactive sample", "Fill this page in KillerPDF",
             "The fields below are real AcroForm widgets. Save a copy to try the form workflow.")
    c.setFillColor(INK)
    c.setFont("Bold", 9)
    c.drawString(MARGIN, y - 20, "Your name")
    c.acroForm.textfield(name="reader_name", tooltip="Your name", x=MARGIN, y=y - 61,
                         width=330, height=29, borderStyle="solid", borderWidth=0.8,
                         borderColor=LINE, fillColor=colors.white, textColor=INK,
                         fontName="Helvetica", fontSize=10)
    c.drawString(MARGIN, y - 99, "What would you like to try next?")
    c.acroForm.textfield(name="next_task", tooltip="Next task", x=MARGIN, y=y - 141,
                         width=480, height=29, borderStyle="solid", borderWidth=0.8,
                         borderColor=LINE, fillColor=colors.white, textColor=INK,
                         fontName="Helvetica", fontSize=10)
    c.acroForm.checkbox(name="tried_form", tooltip="I saved a completed form",
                        x=MARGIN, y=y - 192, size=16, buttonStyle="check",
                        borderWidth=0.8, borderColor=GREEN, fillColor=colors.white)
    c.setFont("Body", 9)
    c.drawString(MARGIN + 26, y - 188, "I saved a completed form.")
    para(c, "This demonstration is intentionally small. It verifies text fields and a checkbox "
         "without asking you to enter sensitive information.", MARGIN, y - 240,
         W - 2 * MARGIN, SMALL)
    c.showPage()

    # 15: Back cover.
    gradient(c, 0, 0, W, H, "#15241b", "#0c1510")
    c.setFillColor(GREEN)
    c.setFont("Bold", 9)
    c.drawString(MARGIN, H - 90, "KEEP EXPLORING")
    c.setFillColor(colors.white)
    c.setFont("Bold", 30)
    c.drawString(MARGIN, H - 135, "Made for the PDFs you have.")
    c.setFont("Body", 12)
    c.drawString(MARGIN, H - 166, "Open source. Local. Built for Windows.")
    for i, (label, url) in enumerate([
        ("Help and keyboard map", "https://killerpdf.net/help.html"),
        ("Technical guide", "https://killerpdf.net/technical.html"),
        ("Source code", "https://github.com/SteveTheKiller/KillerPDF"),
    ]):
        yy = H - 258 - i * 75
        rounded(c, MARGIN, yy - 25, W - 2 * MARGIN, 58, colors.HexColor("#233b2a"), 8)
        c.setFillColor(YELLOW if i == 0 else GREEN)
        c.setFont("Bold", 10)
        c.drawString(MARGIN + 15, yy + 11, label)
        c.setFillColor(colors.white)
        c.setFont("Body", 8)
        c.drawString(MARGIN + 15, yy - 8, url)
        c.linkURL(url, (MARGIN, yy - 25, W - MARGIN, yy + 33), relative=0)
    c.setFillColor(colors.HexColor("#b9c8bc"))
    c.setFont("Body", 8)
    c.drawString(MARGIN, 54, "KillerPDF 1.8  |  Steve the Killer  |  GPLv3")
    c.showPage()
    c.save()
    print(f"Created {OUTPUT} with 15 pages, {len(rows)} shortcut rows, "
          f"{len(lang)} locales, and {len(names)} themes")


if __name__ == "__main__":
    build()
