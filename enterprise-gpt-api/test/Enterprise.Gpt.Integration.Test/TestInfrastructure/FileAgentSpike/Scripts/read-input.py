# US-001, third and fourth acceptance criteria: which of the two ways of handing the sandbox a file
# actually delivers it.
#
# The script does not know which way was used. It reports what is on disk, and the harness decides
# what that means for the input it supplied - which is the only way to tell "the client dropped it"
# apart from "the client sent it and the sandbox could not read it".
import json
import os

MOUNT = "/mnt/data"
MARKER = "__MARKER__"

# .py is skipped on purpose. If the interpreter ever staged this program under the mount, its own
# source would contain the marker and the probe would report a delivered input that was never
# delivered - which is exactly the false pass the DataContent case exists to rule out.
entries = []
for name in sorted(os.listdir(MOUNT)):
    path = os.path.join(MOUNT, name)
    if os.path.isfile(path) and not name.lower().endswith(".py"):
        entries.append({"name": name, "bytes": os.path.getsize(path)})

found = None
for entry in entries:
    path = os.path.join(MOUNT, entry["name"])
    try:
        with open(path, "r", encoding="utf-8", errors="ignore") as handle:
            if MARKER in handle.read():
                found = entry["name"]
                break
    except OSError:
        continue

result = {"marker": MARKER, "markerFoundIn": found, "entries": entries}

with open("/mnt/data/input-probe-result.json", "w", encoding="utf-8") as handle:
    json.dump(result, handle)

print("RESULT:" + json.dumps(result))
