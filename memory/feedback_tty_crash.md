---
name: feedback_tty_crash
description: Fixed tty.lua:74 crash and infinite boot loop
type: feedback
---

Fixed tty.lua:74 crash that occurred when core.read returned false/nil (EOF, interrupted, or unexpected signals). Replaced the assert(ok, result, reason) with safe error handling that returns nil on failure.

Also fixed the boot loop caused by os.sleep(0.1) yielding and immediately resuming every frame, creating a tight loop that burned 100% CPU. Added sleepUntil tracking in Tick() so coroutines only resume after the sleep duration elapses.

Prevented infinite init.lua retry loop by replacing the while true do xpcall with a single attempt. Shell errors now print once instead of retrying thousands of times.

Added readLine support to C-side io.stdin stub so sh.lua:25's io.stdin:readLine(false) doesn't crash.