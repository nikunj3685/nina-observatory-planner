# Changelog

## 1.0.0.0

First release, for N.I.N.A. 3.2.0.9001 and later.

- **Night loop.** Four stages, each a plain Advanced Sequencer instruction set: 1 Begin, 2 Start of each target, 3 While imaging (triggers), 4 End. With a safety monitor it waits for safe, runs 1 Begin, images the targets in priority order and runs 4 End when it turns unsafe or the night is done, then waits for safe and resumes where it stopped. Without a safety monitor it makes one pass from astronomical dusk.
- **Built-in workflows:** Safety and dome, No safety with dome, No safety no dome. Power switches are found on the switch hub by name.
- **Targets.** Start and end by clock time or altitude, rotation, exposure rows with priority, rotate through filters or finish each row first, Delay first and Delay between, progress saved after every frame and editable.
- **Target Settings** with Slew now and Center now (with a collision warning for targets below the horizon or under 10°), Get from Framing Assistant or planetarium, and **Planning tools**: a 24-hour altitude chart for tonight with twilight, the Moon and a "now" marker; click to set the start, right-click to set the end.
- **Pause** now or after this frame, then **Start sequence**: checks connections and safety; on the same target with the mount unmoved it only restarts guiding, otherwise 2 Start of target runs first. Unsafe while paused shuts down and comes back paused. **Stop** runs 4 End.
- **Options:** wait after safe, keep devices connected, gap handling between targets (keep tracking, park or find home, optional dome close), manual autofocus before the next frame, confirmations, auto-start when NINA starts.
- Per-profile target lists, workflows and options; the last workflow is reopened at startup and saved automatically when it changes.
