# Code Review — `feature/sound-feedback` (high effort)

Scope: diff `main..feature/sound-feedback` — `Sounds.cs`, `Settings.cs`,
`TrayAppContext.cs`, `SettingsForm.cs`.

Findings ranked most-significant first.

---

### 1. Blocked-key sound can go permanently silent on a high-uptime machine — [low · correctness · CONFIRMED]
- **File:** `src/TrayAppContext.cs:39, 188-194`
- **Failure scenario:** `_lastBlockedSoundTick` defaults to `0`, and the throttle is
  `unchecked(Environment.TickCount - _lastBlockedSoundTick) >= 700`. `Environment.TickCount`
  is a signed 32-bit counter that goes **negative** after ~24.9 days of uptime. While it is
  negative, `now - 0` is negative, the `>= 700` test is always false, no sound plays, and —
  because the field is only updated *when a sound plays* — it stays `0` forever. So on a PC
  that has been up longer than ~24.9 days, the blocked-key cue never plays for the whole
  ~24.9-day negative window, even with the toggle on.
- **Suggested solution:** Seed the field in the constructor the same way the toggle debounce
  already does: `_lastBlockedSoundTick = unchecked(Environment.TickCount - 700);`. That makes
  the first blocked key always play and seeds the field to a real tick value.

### 2. Lock/unlock cue also fires on automatic unlocks — [low · UX · CONFIRMED]
- **File:** `src/TrayAppContext.cs:144, 161`
- **Failure scenario:** `Sounds.Lock()`/`Unlock()` play on every `SetLocked` transition,
  including non-user-initiated ones (trial expiry now; timed/auto-unlock if those branches
  merge). The keyboard auto-unlocking could emit an unexpected sound at an awkward moment.
- **Suggested solution:** Acceptable if intended. If you want cues only for user-initiated
  changes, thread a `userInitiated` flag through `SetLocked` (or play sounds only from the
  `ToggleLock`/hotkey/menu entry points) rather than inside `SetLocked`.

### 3. Silent when the mapped Windows sounds are set to "(None)" — [low · UX · note]
- **File:** `src/Sounds.cs:11-13`
- **Failure scenario:** `SystemSounds.Exclamation/Asterisk/Hand` play the user's configured
  scheme sounds; if those events are set to "(None)" in Windows, nothing plays even with the
  CatFoil toggle enabled, which can read as "the feature is broken".
- **Suggested solution:** Note in the settings tooltip that it uses the Windows system
  sounds (so users know where to change/enable them). No code fix required.
