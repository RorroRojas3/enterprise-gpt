---
name: pptx-authoring
description: Build slide decks with python-pptx. Use when producing or editing a .pptx.
---

# Authoring .pptx

`python-pptx` 1.0.2 is installed.

## Create

```python
from pptx import Presentation
from pptx.util import Inches

prs = Presentation()

title_slide = prs.slides.add_slide(prs.slide_layouts[0])
title_slide.shapes.title.text = "Meeting Summary"
title_slide.placeholders[1].text = "Prepared from your notes"

for heading, bullets in sections:
    slide = prs.slides.add_slide(prs.slide_layouts[1])
    slide.shapes.title.text = heading
    body = slide.placeholders[1].text_frame
    body.text = bullets[0]
    for bullet in bullets[1:]:
        paragraph = body.add_paragraph()
        paragraph.text = bullet
        paragraph.level = 1

prs.save("/mnt/data/meeting-summary.pptx")
```

## Layouts in the default template

`0` title, `1` title and content, `5` title only, `6` blank. Anything else risks an `IndexError` on a template that does not define it.

## Notes

- A text frame's first paragraph already exists — set `text_frame.text` for it and `add_paragraph()` for the rest, or the first bullet comes out blank.
- Slide size defaults to 4:3. For widescreen set `prs.slide_width = Inches(13.333)` and `prs.slide_height = Inches(7.5)` before adding slides.
- Charts: build the image with `matplotlib` 3.6.3, save it to `/mnt/data`, then `slide.shapes.add_picture(...)`.
