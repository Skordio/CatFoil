# Code Review — `feature/lock-stats` (high effort)

Scope: diff `main..feature/lock-stats` — `StatsForm.cs`, `Settings.cs`,
`TrayAppContext.cs`.

No correctness or data-loss bugs of note; the arithmetic (uint tick subtraction
for elapsed time) is wrap-safe. Findings below are all low severity.

---

### 1. Session's locked time and blocked-key tally are lost on a crash/kill while locked — [low · durability · CONFIRMED]
- **File:** `src/TrayAppContext.cs:145-147` (lock) and `:165-167, 188` (unlock/blocked)
- **Failure scenario:** The session count is saved at lock, but the elapsed time is only
  added on unlock and `StatBlockedKeys` is incremented in memory and only persisted on
  unlock. A clean exit persists them (ExitApp unlocks first), but a crash or force-kill
  while locked loses that session's time and blocked keys — leaving a counted session with
  0 added time (slight inconsistency).
- **Suggested solution:** Flush the running session's stats periodically (piggyback an
  existing timer) and/or on `SystemEvents.SessionEnding`; or accept as a documented minor
  limitation given locks are usually ended normally.

### 2. Full settings.json is rewritten on every lock and unlock — [low · efficiency · CONFIRMED]
- **File:** `src/TrayAppContext.cs:147, 167`
- **Failure scenario:** Each lock and unlock serializes the entire settings file (on top of
  the overlay-drag saves). Harmless at human frequency, but stats are the only truly
  high-churn values in that file.
- **Suggested solution:** If write frequency ever matters, store the volatile counters in a
  separate small file, or debounce/batch the writes.

### 3. Stats dialog is shown with a possibly-hidden owner — [low · robustness · PLAUSIBLE]
- **File:** `src/TrayAppContext.cs:246-249`
- **Failure scenario:** Opened from the tray with the main window closed to tray,
  `StatsForm.ShowDialog(_mainForm)` uses a hidden owner. It works because the handle exists,
  but `CenterParent` positioning relative to a hidden window may land oddly on some setups.
- **Suggested solution:** When the owner isn't visible, use `StartPosition = CenterScreen`
  (or show without an owner). Verify placement when launched from the tray.

### 4. In-progress lock time isn't reflected until unlock — [low · UX · CONFIRMED]
- **File:** `src/TrayAppContext.cs:165-167`
- **Failure scenario:** "Times locked" increments at lock, but "Total time locked" only
  grows on unlock. Opening Statistics *while locked* shows the current session counted but
  its running time missing.
- **Suggested solution:** Acceptable; optionally add the current in-progress elapsed time to
  the displayed total when the keyboard is locked.
