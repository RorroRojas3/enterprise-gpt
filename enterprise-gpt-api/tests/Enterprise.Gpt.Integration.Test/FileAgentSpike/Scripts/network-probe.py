# US-001, second acceptance criterion: confirm the sandbox has no outbound network in THIS
# deployment, rather than inheriting the claim from documentation about a different one.
#
# Two independent attempts, because they fail differently: a raw socket connect proves the egress
# path is closed, while pip proves the package index is unreachable, which is what makes the library
# inventory (US-002) a fixed fact rather than something a skill could install its way around.
import json
import socket
import subprocess
import sys
import urllib.request

result = {}

try:
    with urllib.request.urlopen("https://example.com", timeout=15) as response:
        result["http"] = {"blocked": False, "status": response.status}
except Exception as error:  # noqa: BLE001 - the exception type IS the finding
    result["http"] = {"blocked": True, "error": type(error).__name__, "message": str(error)[:300]}

try:
    socket.create_connection(("1.1.1.1", 443), timeout=15).close()
    result["socket"] = {"blocked": False}
except Exception as error:  # noqa: BLE001
    result["socket"] = {"blocked": True, "error": type(error).__name__, "message": str(error)[:300]}

try:
    completed = subprocess.run(
        [sys.executable, "-m", "pip", "install", "--no-input", "cowsay"],
        capture_output=True,
        text=True,
        timeout=120,
    )
    tail = (completed.stderr or completed.stdout or "").strip().splitlines()[-3:]
    result["pip"] = {"blocked": completed.returncode != 0, "returncode": completed.returncode, "tail": tail}
except Exception as error:  # noqa: BLE001
    result["pip"] = {"blocked": True, "error": type(error).__name__, "message": str(error)[:300]}

with open("/mnt/data/network-probe-result.json", "w", encoding="utf-8") as handle:
    json.dump(result, handle)

print("RESULT:" + json.dumps(result))
