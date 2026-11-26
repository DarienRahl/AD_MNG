#!/usr/bin/env python3
"""Prints what matters of a .NET crash report (DOTNET_EnableCrashReport, the *.crashreport.json next to a mini dump).

The lab job of CI used to print the first 20,000 bytes of the report: the process name and the first threads, which are
mostly waiting - the crashed thread was somewhere in the other 180,000. This prints, for the thread that crashed, every
frame (managed: the method; native: the function if the runtime knew it, always the module and the offset in it - enough
to see which library aborted), then for every other thread its first frames, and the signal data the report carries
besides the threads. The report holds code locations, no directory values.

usage: crash-report-summary.py <report.json> [--max-frames N]
"""
import json
import sys


def is_true(value):
    return value is True or str(value).lower() == "true"


def describe(frame):
    if is_true(frame.get("is_managed")):
        return frame.get("method_name") or f"(managed, token {frame.get('token', '?')})"
    name = frame.get("unmanaged_name") or "?"
    module = frame.get("native_module") or "?"
    return f"{name}  [{module} + {frame.get('native_image_offset') or frame.get('native_offset') or '?'}]"


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    path = argv[1]
    limit = int(argv[argv.index("--max-frames") + 1]) if "--max-frames" in argv else 80
    try:
        with open(path, encoding="utf-8") as stream:
            data = json.load(stream)
    except (OSError, ValueError) as error:
        print(f"{path}: cannot be read as JSON ({error}); the first 2000 characters:")
        try:
            with open(path, encoding="utf-8", errors="replace") as stream:
                print(stream.read(2000))
        except OSError:
            pass
        return 0

    payload = data.get("payload", data) if isinstance(data, dict) else {}
    configuration = payload.get("configuration", {})
    print(f"process {payload.get('process_name')}, runtime {configuration.get('version')}, {configuration.get('architecture')}")
    for source in (data, payload):
        for key, value in source.items():
            if key not in ("payload", "threads", "configuration", "process_name", "protocol_version"):
                print(f"{key}: {json.dumps(value)[:400]}")

    threads = payload.get("threads", [])
    crashed = [t for t in threads if is_true(t.get("crashed"))]
    print(f"{len(threads)} threads, {len(crashed)} marked as crashed")
    for thread in crashed:
        frames = thread.get("stack_frames", [])
        print(f"--- CRASHED thread {thread.get('native_thread_id')} ({len(frames)} frames) ctx {json.dumps(thread.get('ctx'))}")
        for frame in frames[:limit]:
            print("   ", describe(frame))
        if len(frames) > limit:
            print(f"    ... {len(frames) - limit} more")
    for thread in threads:
        if is_true(thread.get("crashed")):
            continue
        frames = thread.get("stack_frames", [])
        managed = [f for f in frames if is_true(f.get("is_managed")) and f.get("method_name")]
        modules = sorted({f.get("native_module") for f in frames if f.get("native_module")})
        print(f"--- thread {thread.get('native_thread_id')}: {len(frames)} frames, native modules: {', '.join(modules) or '-'}")
        for frame in (managed[:6] if managed else frames[:4]):
            print("   ", describe(frame))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
