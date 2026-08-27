# US-002: what the sandbox image actually carries, as against what the PRD assumed it carries.
#
# Every library the proposed skills would name is imported here by its real import name, which is not
# always its package name - python-docx imports as docx, python-pptx as pptx, PyMuPDF as fitz, Pillow
# as PIL. Getting that mapping wrong would report a present library as missing.
#
# The fpdf entry matters more than the others: the PRD records that the published inventory carries
# 1.x rather than fpdf2, and a skill written against the 2.x API would fail at authoring time rather
# than at review time. The version this reports is what settles it.
import importlib
import json
import os
import shutil
import subprocess
import sys

# (import name, distribution name a skill would cite)
LIBRARIES = [
    ("docx", "python-docx"),
    ("openpyxl", "openpyxl"),
    ("pptx", "python-pptx"),
    ("pandas", "pandas"),
    ("reportlab", "reportlab"),
    ("fpdf", "fpdf / fpdf2"),
    ("pypdf", "pypdf"),
    ("PyPDF2", "PyPDF2"),
    ("pdfplumber", "pdfplumber"),
    ("fitz", "PyMuPDF"),
    ("weasyprint", "weasyprint"),
    ("PIL", "Pillow"),
    ("markdown", "markdown"),
    ("tabulate", "tabulate"),
    ("bs4", "beautifulsoup4"),
    ("lxml", "lxml"),
    ("matplotlib", "matplotlib"),
]


def version_of(module):
    for attribute in ("__version__", "VERSION", "version"):
        value = getattr(module, attribute, None)
        if isinstance(value, str):
            return value
        if isinstance(value, tuple):
            return ".".join(str(part) for part in value)
    try:
        from importlib import metadata

        return metadata.version(module.__name__)
    except Exception:  # noqa: BLE001 - an unknown version is a finding, not a failure
        return None


libraries = []
for import_name, package in LIBRARIES:
    try:
        module = importlib.import_module(import_name)
        libraries.append(
            {
                "module": import_name,
                "package": package,
                "importable": True,
                "version": version_of(module),
            }
        )
    except Exception as error:  # noqa: BLE001
        libraries.append(
            {
                "module": import_name,
                "package": package,
                "importable": False,
                "version": None,
                "error": type(error).__name__,
            }
        )

# The single finding that most widens what EP-4 can promise: with a headless office suite on PATH,
# every Office-to-PDF cell can be re-attempted through it and promoted from structural to faithful.
office = {"present": False, "path": None, "version": None}
for candidate in ("soffice", "libreoffice", "lowriter"):
    found = shutil.which(candidate)
    if not found:
        continue
    office["present"] = True
    office["path"] = found
    try:
        completed = subprocess.run([found, "--version"], capture_output=True, text=True, timeout=120)
        office["version"] = (completed.stdout or completed.stderr or "").strip()[:200]
    except Exception as error:  # noqa: BLE001
        office["version"] = f"{type(error).__name__}: {error}"[:200]
    break

result = {
    "pythonVersion": sys.version.split()[0],
    "platform": sys.platform,
    "mountExists": os.path.isdir("/mnt/data"),
    "libraries": libraries,
    "officeConverter": office,
}

with open("/mnt/data/inventory.json", "w", encoding="utf-8") as handle:
    json.dump(result, handle, indent=2)

print("RESULT:" + json.dumps(result))
