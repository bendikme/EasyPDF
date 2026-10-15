"""
Builds the PDFs the regression checks open. Run from the repository root:

    python tests/fixtures/make_fixtures.py

Needs PyMuPDF (pip install pymupdf). The output is checked in, so this only has to run again
when a fixture changes. Link annotations are written as raw PDF objects, so each link type is
exactly what the checks expect - including the malicious ones the viewer must refuse.
"""
import os
import sys

import pymupdf

HERE = os.path.dirname(os.path.abspath(__file__))


def out(name):
    return os.path.join(HERE, name)


def add_link(doc, page, rect, body):
    """Adds a /Link annotation with the given action or destination entries."""
    x0, y0, x1, y1 = rect
    h = page.rect.height
    # PDF space has its origin at the bottom left; PyMuPDF rectangles at the top left.
    pdf_rect = f"[{x0} {h - y1} {x1} {h - y0}]"
    xref = doc.get_new_xref()
    doc.update_object(xref, f"<< /Type /Annot /Subtype /Link /Rect {pdf_rect} /Border [0 0 0] {body} >>")
    kind, value = doc.xref_get_key(page.xref, "Annots")
    if kind == "array":
        annots = value[1:-1].strip()
        doc.xref_set_key(page.xref, "Annots", f"[{annots} {xref} 0 R]")
    elif kind == "xref":
        arr_xref = int(value.split()[0])
        current = doc.xref_object(arr_xref).strip()[1:-1].strip()
        doc.update_object(arr_xref, f"[{current} {xref} 0 R]")
    else:
        doc.xref_set_key(page.xref, "Annots", f"[{xref} 0 R]")
    return xref


# "slutt-ø" and "kapittel-å" as UTF-16BE PDF strings: names no C string can carry.
UTF16_END = "<FEFF0073006C007500740074002D00F8>"
UTF16_CHAPTER = "<FEFF006B006100700069007400740065006C002D00E5>"


def text(page, x, y, s, size=12):
    page.insert_text((x, y), s, fontsize=size, fontname="helv")


def links_pdf():
    doc = pymupdf.open()
    for _ in range(3):
        doc.new_page(width=612, height=792)
    p1, p2, p3 = doc[0], doc[1], doc[2]

    text(p1, 72, 72, "Links test document", 20)
    text(p1, 72, 120, "Visit our site")                         # URI annotation
    text(p1, 72, 150, "Plain address https://example.org/plain in the text")  # web link from text only
    text(p1, 72, 180, "Go to page 3")                           # internal GoTo with XYZ
    text(p1, 72, 210, "Named destination")                      # /Dest /chapter2
    text(p1, 72, 240, "Mail us")                                # mailto
    text(p1, 72, 270, "Launch other.pdf")                       # Launch
    text(p1, 72, 300, "Remote page 2")                          # GoToR page index
    text(p1, 72, 330, "Remote named intro")                     # GoToR named dest
    text(p1, 72, 360, "JavaScript action")                      # unsupported action, no link
    text(p1, 72, 390, "Script address")                         # URI javascript: - refused
    text(p1, 72, 420, "Run calculator")                         # Launch calc.exe - refused
    text(p1, 72, 450, "Two line")                               # quadpoints link, line one
    text(p1, 72, 480, "quad link")                              # quadpoints link, line two
    text(p1, 72, 510, "Relative file link")                     # URI with a relative path to a PDF
    text(p1, 72, 540, "Other page 2 by URI")                    # URI other.pdf#page=2
    text(p1, 72, 570, "Other intro by URI")                     # URI other.pdf#nameddest=intro

    add_link(doc, p1, (70, 106, 200, 124), "/A << /S /URI /URI (https://example.com/docs?a=1&b=2) >>")
    add_link(doc, p1, (70, 166, 200, 184), f"/Dest [{p3.xref} 0 R /XYZ 72 500 0]")
    add_link(doc, p1, (70, 196, 220, 214), "/Dest /chapter2")
    add_link(doc, p1, (70, 226, 200, 244), "/A << /S /URI /URI (mailto:support@example.com) >>")
    add_link(doc, p1, (70, 256, 220, 274), "/A << /S /Launch /F (other.pdf) >>")
    add_link(doc, p1, (70, 286, 220, 304), "/A << /S /GoToR /F (other.pdf) /D [1 /Fit] >>")
    add_link(doc, p1, (70, 316, 240, 334), "/A << /S /GoToR /F (other.pdf) /D (intro) >>")
    add_link(doc, p1, (70, 346, 240, 364), "/A << /S /JavaScript /JS (app.alert\\(1\\)) >>")
    add_link(doc, p1, (70, 376, 240, 394), "/A << /S /URI /URI (javascript:alert\\(1\\)) >>")
    add_link(doc, p1, (70, 406, 240, 424), "/A << /S /Launch /F (calc.exe) >>")
    # Quadrilaterals for two separate words on two lines: only they are clickable, not the box around both.
    h = 792
    q1 = f"72 {h - 438} 140 {h - 438} 72 {h - 454} 140 {h - 454}"
    q2 = f"72 {h - 468} 150 {h - 468} 72 {h - 484} 150 {h - 484}"
    add_link(doc, p1, (70, 436, 300, 486), f"/QuadPoints [{q1} {q2}] /A << /S /URI /URI (https://example.com/quad) >>")
    add_link(doc, p1, (70, 496, 240, 514), "/A << /S /URI /URI (other.pdf) >>")
    add_link(doc, p1, (70, 526, 240, 544), "/A << /S /URI /URI (other.pdf#page=2) >>")
    add_link(doc, p1, (70, 556, 240, 574), "/A << /S /URI /URI (other.pdf#nameddest=intro) >>")

    text(p2, 72, 72, "Chapter 2", 20)
    text(p2, 72, 420, "Section 2.1 starts here")
    text(p2, 72, 120, "Back to page 1")
    add_link(doc, p2, (70, 106, 220, 124), f"/Dest [{p1.xref} 0 R /Fit]")
    # An area of page 3 (/FitR left bottom right top, in PDF space): the frame marks just that.
    text(p2, 320, 120, "An area of page 3")
    add_link(doc, p2, (318, 106, 470, 124), f"/Dest [{p3.xref} 0 R /FitR 60 280 400 320]")

    text(p3, 72, 72, "Chapter 3", 20)
    text(p3, 72, 300, "Target of the page 3 link")

    # Named actions: what hyperref's \Acrobatmenu and presentation buttons make.
    text(p2, 72, 600, "Next page (named)")
    text(p2, 72, 630, "Last page (named)")
    text(p3, 72, 600, "First page (named)")
    text(p3, 72, 630, "Previous page (named)")
    text(p3, 72, 660, "Go back (named)")
    add_link(doc, p2, (70, 586, 240, 604), "/A << /S /Named /N /NextPage >>")
    add_link(doc, p2, (70, 616, 240, 634), "/A << /S /Named /N /LastPage >>")
    add_link(doc, p3, (70, 586, 240, 604), "/A << /Type /Action /S /Named /N /FirstPage >>")
    add_link(doc, p3, (70, 616, 240, 634), "/A << /S /Named /N /PrevPage >>")
    add_link(doc, p3, (70, 646, 240, 664), "/A << /S /Named /N /GoBack >>")

    cat = doc.pdf_catalog()
    doc.xref_set_key(cat, "Dests", f"<< /chapter2 [{p2.xref} 0 R /XYZ 0 792 0] >>")

    doc.set_toc([
        [1, "Introduction", 1],
        [1, "Chapter 2", 2],
        [2, "Section 2.1", 2, {"kind": pymupdf.LINK_GOTO, "page": 1, "to": pymupdf.Point(72, 400)}],
        [1, "Chapter 3", 3],
        [1, "Website", 1, {"kind": pymupdf.LINK_URI, "uri": "https://example.com/toc"}],
        [1, "Other intro", 1],
    ])
    # An outline entry into another PDF, at a place only that file can name.
    other_intro = doc.get_outline_xrefs()[5]
    doc.xref_set_key(other_intro, "Dest", "null")
    doc.xref_set_key(other_intro, "A", "<< /S /GoToR /F (other.pdf) /D (intro) >>")
    doc.set_metadata({"title": "Links fixture", "author": "EasyPDF tests", "subject": "Every kind of link"})
    doc.save(out("links.pdf"), garbage=3, deflate=True)

    # The same links with every object inside compressed object streams (nothing readable in the
    # raw file), and again encrypted: named actions must still be found.
    doc.save(out("links-objstm.pdf"), garbage=3, deflate=True, use_objstms=1)
    doc.save(out("links-encrypted.pdf"), garbage=3, deflate=True, use_objstms=1,
             encryption=pymupdf.PDF_ENCRYPT_AES_256, user_pw="secret", owner_pw="owner-secret",
             permissions=pymupdf.PDF_PERM_PRINT | pymupdf.PDF_PERM_COPY)


def buttons_pdf():
    """Buttons that go somewhere, as EPLAN writes its cross-references: push buttons whose
    actions jump. And what is no link: a hidden button, a text field with an action, a button
    that only runs a script."""
    doc = pymupdf.open()
    for _ in range(3):
        doc.new_page(width=612, height=792)
    p1, p2, p3 = doc[0], doc[1], doc[2]
    text(p1, 72, 72, "Buttons", 20)
    text(p2, 72, 72, "Page two", 20)
    text(p3, 72, 72, "Page three", 20)
    text(p3, 72, 392, "The named place")

    def widget(page, rect, name, kind=pymupdf.PDF_WIDGET_TYPE_BUTTON):
        w = pymupdf.Widget()
        w.field_type = kind
        w.field_name = name
        w.rect = pymupdf.Rect(rect)
        if kind == pymupdf.PDF_WIDGET_TYPE_BUTTON:
            w.field_flags = pymupdf.PDF_BTN_FIELD_IS_PUSHBUTTON
        else:
            w.field_value = "typed"
        return page.add_widget(w).xref

    actions = [
        ((70, 100, 200, 120), "jump", f"<< /S /GoTo /D [{p3.xref} 0 R /Fit] /Next << /S /JavaScript /JS (1) >> >>", None),
        ((70, 140, 200, 160), "web", "<< /S /URI /URI (https://example.com/button) >>", None),
        ((70, 180, 200, 200), "remote-page",
         "<< /S /GoToR /F << /Type /Filespec /F (other.pdf) /UF <FEFF006F0074006800650072002E007000640066> >> /D [1 /Fit] /NewWindow true >>", None),
        ((70, 220, 200, 240), "remote-named", "<< /S /GoToR /F (other.pdf) /D (intro) >>", None),
        ((70, 260, 200, 280), "named-place", "<< /S /GoTo /D (place) >>", None),
        ((70, 300, 200, 320), "hidden", f"<< /S /GoTo /D [{p2.xref} 0 R /Fit] >>", "2"),
        ((70, 380, 200, 400), "script", "<< /S /JavaScript /JS (app.alert\\(1\\)) >>", None),
        ((70, 420, 200, 440), "next", "<< /S /Named /N /NextPage >>", None),
        ((70, 500, 200, 520), "chapter", f"<< /S /GoTo /D {UTF16_CHAPTER} >>", None),
        ((70, 540, 200, 560), "remote-utf16", f"<< /S /GoToR /F (other.pdf) /D {UTF16_END} >>", None),
        ((70, 580, 200, 600), "relative-web", "<< /S /URI /URI (relative.html) >>", None),
        ((70, 620, 200, 640), "read-only", f"<< /S /GoTo /D [{p2.xref} 0 R /Fit] >>", None),
    ]
    made = [(widget(p1, rect, name), action, flags) for rect, name, action, flags in actions]
    typed = widget(p1, (70, 340, 200, 360), "typed", pymupdf.PDF_WIDGET_TYPE_TEXT)

    # A widget that is not itself a field: its type comes from the field above it - and, for a
    # second one, its action too.
    parent = doc.get_new_xref()
    doc.update_object(parent, "<< /FT /Btn /Ff 65536 /T (group) >>")
    acting = doc.get_new_xref()
    doc.update_object(acting, f"<< /FT /Btn /Ff 65536 /T (acting) /A << /S /GoTo /D [{p3.xref} 0 R /Fit] >> >>")
    acting_kid = doc.get_new_xref()
    doc.update_object(acting_kid, f"<< /Type /Annot /Subtype /Widget /Rect [70 {792 - 680} 200 {792 - 660}] /P {p1.xref} 0 R "
                                  f"/Parent {acting} 0 R /F 4 >>")
    doc.xref_set_key(acting, "Kids", f"[{acting_kid} 0 R]")
    kid = doc.get_new_xref()
    doc.update_object(kid, f"<< /Type /Annot /Subtype /Widget /Rect [70 {792 - 480} 200 {792 - 460}] /P {p1.xref} 0 R "
                           f"/Parent {parent} 0 R /F 4 /A << /S /GoTo /D [{p2.xref} 0 R /Fit] >> >>")
    doc.xref_set_key(parent, "Kids", f"[{kid} 0 R]")
    kind, annots = doc.xref_get_key(p1.xref, "Annots")
    if kind == "xref":
        annots_xref = int(annots.split()[0])
        doc.update_object(annots_xref, doc.xref_object(annots_xref).rstrip().rstrip("]") + f" {kid} 0 R {acting_kid} 0 R]")
    else:
        doc.xref_set_key(p1.xref, "Annots", annots.rstrip().rstrip("]") + f" {kid} 0 R {acting_kid} 0 R]")
    cat = doc.pdf_catalog()
    kind, form = doc.xref_get_key(cat, "AcroForm")
    form_xref = int(form.split()[0]) if kind == "xref" else None
    fields_holder = form_xref if form_xref else cat
    fields_key = "Fields" if form_xref else "AcroForm/Fields"
    kind, fields = doc.xref_get_key(fields_holder, fields_key)
    doc.xref_set_key(fields_holder, fields_key, fields.rstrip().rstrip("]") + f" {parent} 0 R {acting} 0 R]")

    # The actions go on last: PyMuPDF rewrites a widget as it adds the next.
    for xref, action, flags in made:
        doc.xref_set_key(xref, "A", action)
        if flags:
            doc.xref_set_key(xref, "F", flags)
    doc.xref_set_key(typed, "A", f"<< /S /GoTo /D [{p2.xref} 0 R /Fit] >>")
    read_only = made[-1][0]
    doc.xref_set_key(read_only, "Ff", str(65536 | 1))
    doc.xref_set_key(cat, "Dests", f"<< /place [{p3.xref} 0 R /XYZ 72 400 0] >>")
    doc.xref_set_key(cat, "Names", f"<< /Dests << /Names [{UTF16_CHAPTER} [{p3.xref} 0 R /XYZ 72 400 0]] >> >>")
    doc.xref_set_key(cat, "URI", "<< /Base (https://example.com/base/) >>")

    # Cross-reference and object streams, as EPLAN's PDFs have them; and encrypted, which only
    # PDFium can read.
    doc.save(out("buttons.pdf"), garbage=3, deflate=True, use_objstms=1)
    doc.save(out("buttons-encrypted.pdf"), garbage=3, deflate=True, use_objstms=1,
             encryption=pymupdf.PDF_ENCRYPT_AES_256, user_pw="secret", owner_pw="owner-secret",
             permissions=pymupdf.PDF_PERM_PRINT | pymupdf.PDF_PERM_COPY)


def other_pdf():
    doc = pymupdf.open()
    for i in range(2):
        page = doc.new_page(width=612, height=792)
        text(page, 72, 72, f"Other document page {i + 1}", 20)
    text(doc[1], 72, 400, "Intro destination")
    cat = doc.pdf_catalog()
    doc.xref_set_key(cat, "Dests", f"<< /intro [{doc[1].xref} 0 R /XYZ 72 420 0] >>")
    # A name that is not ASCII, as a UTF-16 string in a name tree.
    doc.xref_set_key(cat, "Names", f"<< /Dests << /Names [{UTF16_END} [{doc[1].xref} 0 R /XYZ 72 420 0]] >> >>")
    doc.save(out("other.pdf"), garbage=3, deflate=True)


def encrypted_pdfs():
    for name, user, owner in (("encrypted.pdf", "secret", "owner-secret"), ("owner-only.pdf", "", "owner-secret")):
        doc = pymupdf.open()
        page = doc.new_page(width=612, height=792)
        text(page, 72, 72, "Encrypted document", 20)
        text(page, 72, 120, "Blåbærsyltetøy and secret words")
        doc.save(out(name), encryption=pymupdf.PDF_ENCRYPT_AES_256, user_pw=user, owner_pw=owner,
                 permissions=pymupdf.PDF_PERM_PRINT | pymupdf.PDF_PERM_COPY)


def big_pdf():
    doc = pymupdf.open()
    for i in range(2000):
        page = doc.new_page(width=612, height=792)
        text(page, 72, 72, f"Page {i + 1} heading", 18)
        for line in range(30):
            word = "needle" if (i in (499, 1499) and line == 10) else "lorem"
            text(page, 72, 110 + line * 20, f"{word} ipsum dolor sit amet line {line + 1} on page {i + 1}", 10)
    doc.save(out("big.pdf"), garbage=3, deflate=True)


def mixed_pdf():
    doc = pymupdf.open()
    sizes = [(612, 792), (842, 595), (612, 792), (100, 100), (2000, 3000)]
    for i, (w, h) in enumerate(sizes):
        page = doc.new_page(width=w, height=h)
        text(page, 10, 30, f"Mixed page {i + 1}", 12)
    # Page 3 is stored portrait and shown turned a quarter clockwise. Text and link go on in the
    # page's own (unturned) coordinates, before it is turned.
    text(doc[2], 72, 100, "Rotated page link")
    add_link(doc, doc[2], (70, 86, 220, 104), "/A << /S /URI /URI (https://example.com/rotated) >>")
    doc[2].set_rotation(90)
    doc.save(out("mixed.pdf"), garbage=3, deflate=True)


def a4_pdf():
    """A4 is 595.28 x 841.89 points: a page size that is not whole points, turned each way."""
    doc = pymupdf.open()
    for rotation in (0, 90, 180, 270):
        page = doc.new_page(width=595.28, height=841.89)
        text(page, 50, 60, f"A4 page turned {rotation}", 18)
        for line in range(40):
            text(page, 50, 100 + line * 18, f"Line {line + 1}: The quick brown fox jumps over the lazy dog 0123456789", 11)
        page.draw_rect(pymupdf.Rect(20, 20, 575.28, 821.89), color=(0.2, 0.3, 0.8), width=1.5)
        page.set_rotation(rotation)
    doc.save(out("a4.pdf"), garbage=3, deflate=True)


def form_pdf():
    doc = pymupdf.open()
    page = doc.new_page(width=612, height=792)
    text(page, 72, 72, "Form document", 20)
    field = pymupdf.Widget()
    field.field_type = pymupdf.PDF_WIDGET_TYPE_TEXT
    field.field_name = "name"
    field.field_value = "Hello form"
    field.rect = pymupdf.Rect(72, 100, 300, 130)
    field.text_fontsize = 14
    page.add_widget(field)
    box = pymupdf.Widget()
    box.field_type = pymupdf.PDF_WIDGET_TYPE_CHECKBOX
    box.field_name = "agree"
    box.field_value = True
    box.rect = pymupdf.Rect(72, 150, 92, 170)
    page.add_widget(box)
    doc.save(out("form.pdf"), garbage=3, deflate=True)


def search_pdf():
    doc = pymupdf.open()
    page = doc.new_page(width=612, height=792)
    text(page, 72, 72, "alpha beta Alpha gamma alphabet ALPHA", 12)
    text(page, 72, 100, "second line with alpha", 12)
    page = doc.new_page(width=612, height=792)
    text(page, 72, 72, "page two alpha", 12)
    doc.save(out("search.pdf"), garbage=3, deflate=True)


def cyclic_outline_pdf():
    doc = pymupdf.open()
    for i in range(2):
        text(doc.new_page(width=612, height=792), 72, 72, f"Cyclic page {i + 1}", 20)
    doc.set_toc([[1, "First", 1], [1, "Second", 2]])
    # Point the last entry back at the first: a reader that follows /Next blindly loops forever.
    outlines = int(doc.xref_get_key(doc.pdf_catalog(), "Outlines")[1].split()[0])
    first = int(doc.xref_get_key(outlines, "First")[1].split()[0])
    last = int(doc.xref_get_key(outlines, "Last")[1].split()[0])
    doc.xref_set_key(last, "Next", f"{first} 0 R")
    # And give the first a child that is its own parent's sibling chain.
    doc.xref_set_key(first, "First", f"{first} 0 R")
    doc.xref_set_key(first, "Count", "1")
    doc.save(out("cyclic-outline.pdf"))


def broken_files():
    with open(out("links.pdf"), "rb") as f:
        data = f.read()
    with open(out("truncated.pdf"), "wb") as f:
        f.write(data[: len(data) // 3])
    with open(out("notpdf.pdf"), "wb") as f:
        f.write(b"This is a plain text file pretending to be a PDF.\n" * 20)
    with open(out("empty.pdf"), "wb") as f:
        pass
    with open(out("garbage.pdf"), "wb") as f:
        f.write(b"%PDF-1.7\n" + bytes((i * 37 + 11) % 256 for i in range(20000)))


if __name__ == "__main__":
    links_pdf()
    buttons_pdf()
    other_pdf()
    encrypted_pdfs()
    mixed_pdf()
    a4_pdf()
    form_pdf()
    search_pdf()
    cyclic_outline_pdf()
    broken_files()
    for name in sorted(os.listdir(HERE)):
        if name.endswith(".pdf"):
            print(f"{os.path.getsize(out(name)):>10}  {name}")
    sys.exit(0)
