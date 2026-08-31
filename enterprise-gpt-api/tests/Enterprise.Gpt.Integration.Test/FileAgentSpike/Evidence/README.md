# EP-0 evidence

What a recording run of the File Agent capability gate leaves behind. Both files are written by
`dotnet test` with `FileAgentSpike:Record` set, reviewed by a person, and then committed — after
which every later run diffs against them instead of overwriting them.

| File | Written by | What later runs do with it |
| --- | --- | --- |
| `capability-report.json` | US-001, at fixture teardown | Read by people. It is the record of how files reach the sandbox and how artifacts come back, which is what EP-2 builds on. |
| `sandbox-inventory.json` | US-002 | Diffed against the live image. A library that appears, disappears, or changes major version fails the run and is named. |

Neither file may contain a key, a token, an endpoint path or a user's content. The capability report
records the deployment name and the resource authority and nothing else about the connection.

Nothing lands here until the gate is run against a live deployment — see
[docs/tools/file-agent.md](../../../../../docs/tools/file-agent.md).
