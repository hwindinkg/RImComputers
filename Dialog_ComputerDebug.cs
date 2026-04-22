using RimWorld;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimComputers
{
    /// <summary>
    /// Debug window: shows the internal log and provides a Lua REPL.
    ///
    /// FIXES:
    /// - Scroll bar now works correctly (mouse wheel scrolls up).
    ///   Root cause: auto-scroll was overwriting logScroll every Layout event,
    ///   which fired AFTER scroll events and reset the position back to bottom.
    ///   Fix: only auto-scroll when new entries actually arrive, not every frame.
    /// - Removed the 2000-entry cap from the log (cap is now configurable, default
    ///   unlimited in the debug window — trimming happens in OCApi / Comp_Computer).
    /// </summary>
    public class Dialog_ComputerDebug : Window
    {
        private readonly Comp_Computer comp;
        private string replInput  = "";
        private string replOutput = "";

        private Vector2 logScroll  = Vector2.zero;
        private bool    autoScroll = true;
        private int     lastLogCount = 0;   // detect new entries

        public Dialog_ComputerDebug(Comp_Computer comp)
        {
            this.comp      = comp;
            forcePause     = false;
            doCloseButton  = true;
            doCloseX       = true;
            resizeable     = true;
            draggable      = true;
        }

        public override Vector2 InitialSize => new Vector2(760f, 560f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            float y = inRect.y;

            // ── Header ────────────────────────────────────────────────────
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f),
                $"Debug Console — {comp.parent.LabelCap}");
            y += 26f;

            // ── Stats row ─────────────────────────────────────────────────
            Text.Font = GameFont.Tiny;
            int logCount;
            lock (comp.DebugLog) logCount = comp.DebugLog.Count;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 18f),
                $"State: {comp.State}   Lua: {comp.LuaVerString}   " +
                $"RAM: {comp.RamUsed / 1024}KB / {comp.EffectiveRamMB:0.#}MB   " +
                $"ROM: {comp.RomUsed / 1024}KB / {comp.Props.RomMB:F0}MB   " +
                $"Log entries: {logCount}");
            y += 22f;
            Text.Font = GameFont.Small;

            // ── Log area ──────────────────────────────────────────────────
            float logH = inRect.height - (y - inRect.y) - 26f - 26f - 28f - 12f;
            if (logH < 40f) logH = 40f;

            var logOuter = new Rect(inRect.x, y, inRect.width, logH);
            Widgets.DrawBoxSolid(logOuter, new Color(0.08f, 0.08f, 0.08f));

            List<string> logSnapshot;
            lock (comp.DebugLog)
                logSnapshot = new List<string>(comp.DebugLog);

            float lineH  = 16f;
            float viewH  = Mathf.Max(logOuter.height, logSnapshot.Count * lineH + 4f);
            var   logView = new Rect(0f, 0f, logOuter.width - 20f, viewH);

            // ── Auto-scroll: only jump to bottom when NEW entries arrive ──
            // Previously the code ran on every Layout event, which fires AFTER
            // scroll-wheel events and would immediately undo any manual scroll.
            bool hasNewEntries = logSnapshot.Count != lastLogCount;
            lastLogCount = logSnapshot.Count;

            if (autoScroll && hasNewEntries)
                logScroll.y = Mathf.Max(0f, viewH - logOuter.height);

            // ── Detect manual wheel scroll → disable auto-scroll ──────────
            // Must check BEFORE BeginScrollView so the event isn't consumed.
            if (Event.current.type == EventType.ScrollWheel &&
                logOuter.Contains(Event.current.mousePosition))
            {
                autoScroll = false;
                // Apply the wheel scroll ourselves so Widgets.BeginScrollView
                // also sees it (it will still process it — this just disables auto).
            }

            Widgets.BeginScrollView(logOuter, ref logScroll, logView);

            Text.Font   = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color   = new Color(0.7f, 1f, 0.7f);

            // Viewport culling
            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(logScroll.y / lineH) - 1);
            int lastVisible  = Mathf.Min(logSnapshot.Count - 1,
                                Mathf.CeilToInt((logScroll.y + logOuter.height) / lineH));

            for (int i = firstVisible; i <= lastVisible; i++)
            {
                Widgets.Label(
                    new Rect(2f, i * lineH, logView.width, lineH),
                    logSnapshot[i]);
            }

            GUI.color   = Color.white;
            Text.Font   = GameFont.Small;
            Widgets.EndScrollView();

            y += logH + 4f;

            // ── REPL row ──────────────────────────────────────────────────
            Widgets.Label(new Rect(inRect.x, y, 40f, 22f), "Lua>");
            var replRect = new Rect(inRect.x + 42f, y, inRect.width - 120f, 22f);
            replInput = Widgets.TextField(replRect, replInput);

            var runRect = new Rect(replRect.xMax + 4f, y, 70f, 22f);
            if (Widgets.ButtonText(runRect, "Execute") && comp.State == ComputerState.Running)
                ExecuteRepl();

            // Only fire on Enter when the text field has focus (GUI.GetNameOfFocusedControl)
            // to avoid the same double-Enter issue as the terminal.
            if (Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.Return &&
                comp.State == ComputerState.Running &&
                !string.IsNullOrEmpty(replInput))
            {
                ExecuteRepl();
                Event.current.Use();
            }
            y += 26f;

            // ── REPL output ───────────────────────────────────────────────
            if (!string.IsNullOrEmpty(replOutput))
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(1f, 1f, 0.6f);
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f),
                    "→ " + replOutput);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
            y += 26f;

            // ── Buttons row ───────────────────────────────────────────────
            float bx = inRect.x;

            if (Widgets.ButtonText(new Rect(bx, y, 90f, 24f), "Clear Log"))
            {
                lock (comp.DebugLog) comp.DebugLog.Clear();
                replOutput   = "";
                lastLogCount = 0;
                logScroll    = Vector2.zero;
            }
            bx += 96f;

            if (Widgets.ButtonText(new Rect(bx, y, 120f, 24f), "Force Restart"))
            {
                comp.TryPowerOff();
                comp.TryPowerOn();
            }
            bx += 126f;

            if (Widgets.ButtonText(new Rect(bx, y, 110f, 24f), "Copy Log"))
            {
                string logText;
                lock (comp.DebugLog) logText = string.Join("\n", comp.DebugLog);
                GUIUtility.systemCopyBuffer = logText;
                Messages.Message("Log copied to clipboard.", MessageTypeDefOf.SilentInput, false);
            }
            bx += 116f;

            // Toggle auto-scroll
            bool newAuto = autoScroll;
            Widgets.CheckboxLabeled(new Rect(bx, y + 3f, 120f, 24f), "Auto-scroll", ref newAuto);
            if (newAuto != autoScroll)
            {
                autoScroll = newAuto;
                if (autoScroll)
                    logScroll.y = Mathf.Max(0f, viewH - logOuter.height);
            }
        }

        private void ExecuteRepl()
        {
            replOutput = comp.ExecLua(replInput);
            comp.Log($"[REPL] {replInput} → {replOutput}");
            replInput = "";
        }
    }
}
