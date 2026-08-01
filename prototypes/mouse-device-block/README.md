# Mouse device block — prototype

Spike for a possible CatFoil mode: block input from ONE pointing device (the
laptop trackpad, say) while any other mouse keeps working. Opt-in, unlockable
by Alt+G or Ctrl+Alt+Del. **Not part of the product; not committed to a plan.**

## What it has to prove

A low-level mouse hook (`WH_MOUSE_LL`) is the only user-mode way to *block*
mouse input, but it cannot tell devices apart — Windows merges every mouse into
one stream first. Raw Input (`WM_INPUT`) is the only way to tell devices apart,
but cannot block. The combo works because eating an event in the hook does NOT
stop its `WM_INPUT` from arriving; Raw Input sees everything.

The unknown is **ordering**: does the `WM_INPUT` attribution for an event
arrive before the hook must decide about that same event?

- If yes → exact per-event attribution is possible; the real feature can be
  precise.
- If no → the sliding-window heuristic here still works (a device's event
  bursts keep a 100 ms "eat" window open), but the first event of a burst
  leaks, and genuinely simultaneous use of two mice can eat a few events from
  the wrong one.

## How to test

```powershell
cd prototypes\mouse-device-block
dotnet run            # or: dotnet run -- --list  (just prints the devices)
```

1. **Monitor first** (nothing is blocked): tick *Monitor attribution*, move
   each mouse, watch the log. `R#n` lines are Raw Input (with device handle),
   `H#n` lines are the hook. Compare the sequence numbers for the same
   physical action: if `R#` < `H#`, raw arrives first and exact attribution is
   possible. Tick *Verbose* to include every move event.
2. **Block**: select the trackpad in the list, click *Block selected device*,
   read the confirm box. Trackpad input should die while the other mouse keeps
   working. Unlock: **Alt+G** (falls back to Alt+Shift+G if CatFoil owns
   Alt+G — the banner says which), the Stop button via the other mouse, or
   just wait — **blocking always auto-stops after 120 s**. Ctrl+Alt+Del also
   always works (hooks don't reach the secure desktop).

## Safety rails (why it's safe to play with)

- The failsafe timer stops blocking after 120 s no matter what.
- Injected input (`LLMHF_INJECTED`) always passes — remote-assistance and
  accessibility tools keep working.
- If attribution is unavailable (Raw Input registration failed, unknown
  device), events PASS. Fail-open by design in a prototype.
- Keyboard is never touched.

## Known limits / notes for the real feature

- Device handles are session-scoped and change on replug — the real feature
  must persist the device *path* (stable) and re-resolve handles on
  arrival/removal (`WM_INPUT_DEVICE_CHANGE`).
- Precision touchpads route through a HID filter stack; most still present a
  raw "mouse" device for pointer moves, but this is exactly what the hand test
  verifies.
- An elevated app's mouse input cannot be eaten by an unelevated hook — same
  rule as the keyboard hook, CatFoil already self-elevates for that.
- If this graduates: the engine bits (`MouseHook`, raw-input attribution)
  mirror `KeyboardHook`'s structure on purpose.
