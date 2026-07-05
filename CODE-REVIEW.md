# Code Review — `feature/timed-lock` (high effort)

Scope: diff `main..feature/timed-lock` — `MainForm.cs`, `TrayAppContext.cs`.

Findings ranked most-significant first.

---

### 1. Dueling countdowns when a timed lock outlasts the free trial (unlicensed) — [medium · correctness · CONFIRMED]
- **File:** `src/TrayAppContext.cs:209-213` (UpdateTimedCountdown) and `:251-260` (trial warning)
- **Failure scenario:** An unlicensed user picks "Lock for 60 minutes". The free trial
  still force-unlocks at 30 minutes, and once ≤120s of the trial remain, `TrialTick`
  writes `ShowTrialCountdown` ("Free session ends in…" + buy link) and `_overlay.SetRemaining`
  every second, while `TimedTick` writes `ShowLockCountdown` ("Auto-unlock in…") and
  `_overlay.SetRemaining` every second. The two handlers fight over the same status label
  and overlay text each tick, producing flicker/contradictory text — and the lock ends at
  the 30-minute trial cap regardless of the chosen 60.
- **Suggested solution:** For unlicensed users, clamp the requested timed duration to the
  remaining trial seconds so a timed lock can never exceed the trial (and thus never
  overlaps the trial warning). Alternatively, make `TimedTick` the single countdown source
  while a timed lock is active and suppress the trial-warning display until it ends.

### 2. "Lock for N" while already locked silently arms an auto-unlock — [low · UX · CONFIRMED]
- **File:** `src/TrayAppContext.cs:190-195`
- **Failure scenario:** The keyboard is already locked (manual/hotkey). The user opens the
  tray and clicks "Lock for 15 minutes" expecting to *extend* protection; instead `LockFor`
  starts a 15-minute timer on the existing lock, so the keyboard silently unlocks 15
  minutes later — leaving the cat an opening.
- **Suggested solution:** If intended, make the armed auto-unlock visible (tray tooltip or a
  checked menu item showing remaining time). If not, only start the timer when the call
  actually initiates a lock, or confirm before converting an indefinite lock to a timed one.

### 3. Timed lock shows a running countdown on the badge for the entire duration — [low · behaviour · CONFIRMED]
- **File:** `src/TrayAppContext.cs:213`
- **Failure scenario:** The trial only shows a countdown in its final 2 minutes, but a
  timed lock calls `_overlay.SetRemaining` from second one, so the badge always displays a
  timer. Not a bug, but an inconsistent look versus the trial.
- **Suggested solution:** If you only want the countdown near the end, gate `SetRemaining`
  behind a threshold as the trial does; otherwise confirm the always-on timer is desired.

### 4. "Lock for…" ellipsis implies a dialog it doesn't have — [low · UX · CONFIRMED]
- **File:** `src/TrayAppContext.cs:98`
- **Failure scenario:** The tray label "Lock for…" (with ellipsis) conventionally signals a
  prompt, but it only opens a submenu of fixed values (5/15/30/60). Minor expectation
  mismatch.
- **Suggested solution:** Drop the ellipsis, or add a "Custom…" submenu entry that prompts
  for a minute count.
