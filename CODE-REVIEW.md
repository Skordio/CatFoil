# Code Review — `feature/auto-lock-idle` (high effort)

Scope: diff `main..feature/auto-lock-idle` — `IdleTime.cs`, `Settings.cs`,
`TrayAppContext.cs`, `SettingsForm.cs`.

Findings ranked most-significant first.

---

### 1. Auto-lock fires during passive full-screen use (video, reading, slides) — [medium · correctness-of-intent · CONFIRMED]
- **File:** `src/TrayAppContext.cs:180-185`
- **Failure scenario:** Idle is measured purely by `GetLastInputInfo` (no keyboard or
  mouse). Someone watching a full-screen movie, on a video call, or reading a long page
  produces no input for the threshold and gets their keyboard locked mid-activity — the
  opposite of what they want. Games that use raw input similarly may not update the idle
  timer.
- **Suggested solution:** Skip auto-lock when a full-screen app is foreground (reuse the
  `ForegroundIsFullscreen()` logic already in `OverlayForm`), and/or when audio/video is
  playing. At minimum, expose a "don't auto-lock during full-screen apps" checkbox
  defaulted on.

### 2. Auto-lock can fire while the user is reading CatFoil's own windows — [low · UX · CONFIRMED]
- **File:** `src/TrayAppContext.cs:180-185`
- **Failure scenario:** With the Settings or Welcome dialog open, a user reading it
  without moving the mouse for the threshold triggers an auto-lock, which is confusing
  while they're mid-configuration.
- **Suggested solution:** Suppress auto-lock while a CatFoil window is the foreground
  window (check `GetForegroundWindow()`'s pid against the current process), or while any
  owned modal dialog is open.

### 3. Idle timer polls every 5s even when the feature is disabled — [low · efficiency · CONFIRMED]
- **File:** `src/TrayAppContext.cs:33, 85-86, 182`
- **Failure scenario:** `_idleTimer` runs unconditionally; when `AutoLockEnabled` is
  false each tick just checks the flag and returns. Harmless but needless wakeups that
  also keep the process slightly busier than necessary.
- **Suggested solution:** Start/stop `_idleTimer` based on `AutoLockEnabled` — start it
  in the ctor only if enabled, and toggle it in the `SettingsSaved` handler when the
  setting changes.

### 4. Threshold re-reads settings each tick but the timer interval is coarse — [low · behaviour · note]
- **File:** `src/TrayAppContext.cs:183-184`
- **Failure scenario:** With a 5s poll, the actual lock can land up to ~5s after the
  configured minute boundary. Not wrong, just imprecise; fine for this feature.
- **Suggested solution:** Acceptable as-is. If tighter timing is ever wanted, lower the
  interval near the threshold. No change required.
