#!/usr/bin/env python3
"""Run samples/smoke.sh against the running sample API and save its real output as a terminal session.

  ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5199 dotnet run --project samples/Blocks.Sample.Api &
  python3 samples/demo/capture_smoke.py
  cd samples/demo && npm install && node terminal.mjs smoke-session.json ../../docs/media smoke-demo
"""
import json, pathlib, subprocess

ROOT = pathlib.Path(__file__).resolve().parents[2]
out = subprocess.run(["bash", "samples/smoke.sh"], cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=True).stdout

# smoke.sh prints "$ <request>" before each section; group the sections into captioned chunks.
sections = [s for s in out.strip().split("\n\n") if s.strip()]
captions = [
    ("/health/ready", "Sample API on :5199: health, register, log in (access + refresh token)"),
    ("Idempotency-Key: order-1) #1", "Idempotency: the same key and body replays the stored response"),
    ("same key, different body", "Same key with a different body is refused (409); no key is a 400"),
    ("/media/hd (no subscription)", "Entitlements: no plan gives 402, checkout + signed webhook, then 200"),
    ("/demo/notify-me", "Notifications: delivered through the outbox into the in-app inbox"),
    ("/auth/logout", "Logout revokes the access token: /auth/me is now 401"),
]
steps, current = [], None
for sec in sections:
    for marker, caption in captions:
        if marker in sec.splitlines()[0]:
            current = {"caption": caption, "out": "", "clear": bool(steps)}
            steps.append(current)
            break
    current["out"] += ("\n" if current["out"] else "") + sec + "\n"
steps[0]["cmd"] = "bash samples/smoke.sh"
for st in steps:
    st["hold"] = 3600

session = {
    "title": "Agentic Building Blocks: sample API smoke test",
    "prompt": "~/blocks $",
    "width": 1180, "height": 720, "fontSize": 14,
    "highlight": [
        [r"^\$ ", "#7ec4ff"], [r"Idempotency-Replayed: true", "#e8d48b"],
        [r"-> 4\d\d|  me -> 401", "#f28b82"], [r"-> 2\d\d|^HTTP/1.1 2|^Healthy|^ - ", "#7fd4a8"],
    ],
    "steps": steps,
}
pathlib.Path(__file__).with_name("smoke-session.json").write_text(json.dumps(session, indent=1))
print(f"{len(steps)} steps captured")
