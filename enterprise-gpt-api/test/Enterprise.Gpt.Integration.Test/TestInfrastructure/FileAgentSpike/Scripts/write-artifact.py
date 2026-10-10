# US-001, first acceptance criterion: does a request carrying HostedCodeInterpreterTool come back
# with a produced artifact at all, over the Responses-route client this application already uses?
#
# Deliberately the smallest possible artifact. The question is whether a file crosses the boundary,
# not whether the sandbox can author anything interesting.
import json
import os

MARKER = "enterprise-gpt-file-agent-ep0"
OUT = "/mnt/data/probe-artifact.txt"

with open(OUT, "w", encoding="utf-8") as handle:
    handle.write(MARKER)

result = {
    "wrote": OUT,
    "bytes": os.path.getsize(OUT),
    "marker": MARKER,
}

with open("/mnt/data/probe-artifact-result.json", "w", encoding="utf-8") as handle:
    json.dump(result, handle)

print("RESULT:" + json.dumps(result))
