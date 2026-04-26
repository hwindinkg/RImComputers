using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimComputers
{
    /// <summary>
    /// Keyboard input capture for the computer terminal.
    ///
    /// FIX: double-Enter bug.
    /// Unity fires TWO events for the Return key on some platforms:
    ///   (1) EventType.KeyDown with keyCode=KeyCode.Return, character='\0'
    ///   (2) EventType.KeyDown with keyCode=KeyCode.None,   character='\n'
    /// We handle Return in the keyCode branch and mark e.Use(), which normally
    /// suppresses (2). But if Unity sends them as separate OnGUI calls the Use()
    /// from call (1) doesn't carry over to call (2).
    /// Solution: track a 1-frame "just handled return" flag to eat the '\n' char event.
    /// </summary>
    public class ComputerInputCapture : MonoBehaviour
    {
        public static Dialog_ComputerScreen TargetWindow { get; set; }

        // Modifier state
        private bool _lctrlDown, _rctrlDown;
        private bool _laltDown,  _raltDown;
        private bool _lshiftDown,_rshiftDown;

        // Guard against the duplicate '\n' character event that Unity fires after Return
        private bool _returnHandledThisFrame = false;

        // OC (DIK) scancodes for modifier keys
        private const int SC_LCONTROL = 0x1D;
        private const int SC_RCONTROL = 0x9D;
        private const int SC_LALT     = 0x38;
        private const int SC_RALT     = 0xB8;
        private const int SC_LSHIFT   = 0x2A;
        private const int SC_RSHIFT   = 0x36;

        public void Update()
        {
            // Reset per-frame guard at the start of every Update (before OnGUI calls)
            _returnHandledThisFrame = false;
        }

        public void OnGUI()
        {
            if (TargetWindow == null) return;

            var e = Event.current;
            if (e == null) return;

            // ── Modifier tracking ────────────────────────────────────────────
            if (e.type == EventType.KeyDown || e.type == EventType.KeyUp)
            {
                bool isDown = e.type == EventType.KeyDown;
                TrackModifier(e.keyCode, KeyCode.LeftControl,  ref _lctrlDown,  SC_LCONTROL, isDown);
                TrackModifier(e.keyCode, KeyCode.RightControl, ref _rctrlDown,  SC_RCONTROL, isDown);
                TrackModifier(e.keyCode, KeyCode.LeftAlt,      ref _laltDown,   SC_LALT,     isDown);
                TrackModifier(e.keyCode, KeyCode.RightAlt,     ref _raltDown,   SC_RALT,     isDown);
                TrackModifier(e.keyCode, KeyCode.LeftShift,    ref _lshiftDown, SC_LSHIFT,   isDown);
                TrackModifier(e.keyCode, KeyCode.RightShift,   ref _rshiftDown, SC_RSHIFT,   isDown);
            }

            if (e.type != EventType.KeyDown) return;

            bool ctrl = _lctrlDown || _rctrlDown;
            bool alt  = _laltDown  || _raltDown;

            KeyCode kc = e.keyCode;

            // ── Special / non-printable keys ──────────────────────────────────
            int specialSc = GetSpecialScancode(kc);
            if (specialSc != 0)
            {
                char ch = GetSpecialChar(kc);
                InputBuffer.EnqueueKeyDown(ch, specialSc);
                InputBuffer.EnqueueKeyUp(ch, specialSc);

                // Mark Return as handled so the companion '\n' char event is eaten
                if (kc == KeyCode.Return || kc == KeyCode.KeypadEnter)
                    _returnHandledThisFrame = true;

                e.Use();
                return;
            }

            // ── Modifier-only keydown ──────────────────────────────────────────
            if (IsModifierKey(kc)) { e.Use(); return; }

            // ── Ctrl+letter ───────────────────────────────────────────────────
            if (ctrl && kc >= KeyCode.A && kc <= KeyCode.Z)
            {
                int sc = LetterScancodes[kc - KeyCode.A];
                InputBuffer.EnqueueKeyDown('\0', sc);
                InputBuffer.EnqueueKeyUp('\0', sc);
                e.Use();
                return;
            }

            // ── Printable characters ──────────────────────────────────────────
            char c = e.character;

            // Eat the duplicate '\n' that Unity fires after Return
            if ((c == '\n' || c == '\r') && _returnHandledThisFrame)
            {
                e.Use();
                return;
            }

            if (c != '\0' && c != '\b' && !ctrl && !alt)
            {
                if (c >= 32 || c == '\n' || c == '\t')
                {
                    int sc = CharToOCScancode(c);
                    InputBuffer.EnqueueKeyDown(c, sc);
                    InputBuffer.EnqueueKeyUp(c, sc);
                    e.Use();
                }
            }
        }

        private static void TrackModifier(KeyCode pressed, KeyCode target,
            ref bool wasDown, int scancode, bool isDown)
        {
            if (pressed != target) return;
            if (isDown  && !wasDown) InputBuffer.EnqueueKeyDown('\0', scancode);
            else if (!isDown && wasDown) InputBuffer.EnqueueKeyUp('\0', scancode);
            wasDown = isDown;
        }

        private static bool IsModifierKey(KeyCode kc)
            => kc == KeyCode.LeftControl  || kc == KeyCode.RightControl  ||
               kc == KeyCode.LeftAlt      || kc == KeyCode.RightAlt      ||
               kc == KeyCode.LeftShift    || kc == KeyCode.RightShift    ||
               kc == KeyCode.LeftCommand  || kc == KeyCode.RightCommand  ||
               kc == KeyCode.LeftWindows  || kc == KeyCode.RightWindows;

        private static int GetSpecialScancode(KeyCode kc)
        {
            switch (kc)
            {
                case KeyCode.Return:       return 0x1C;
                case KeyCode.KeypadEnter:  return 0x9C;
                case KeyCode.Backspace:    return 0x0E;
                case KeyCode.Tab:          return 0x0F;
                case KeyCode.Escape:       return 0x01;
                case KeyCode.Delete:       return 0xD3;
                case KeyCode.Insert:       return 0xD2;
                case KeyCode.Home:         return 0xC7;
                case KeyCode.End:          return 0xCF;
                case KeyCode.PageUp:       return 0xC9;
                case KeyCode.PageDown:     return 0xD1;
                case KeyCode.UpArrow:      return 0xC8;
                case KeyCode.DownArrow:    return 0xD0;
                case KeyCode.LeftArrow:    return 0xCB;
                case KeyCode.RightArrow:   return 0xCD;
                case KeyCode.F1:           return 0x3B;
                case KeyCode.F2:           return 0x3C;
                case KeyCode.F3:           return 0x3D;
                case KeyCode.F4:           return 0x3E;
                case KeyCode.F5:           return 0x3F;
                case KeyCode.F6:           return 0x40;
                case KeyCode.F7:           return 0x41;
                case KeyCode.F8:           return 0x42;
                case KeyCode.F9:           return 0x43;
                case KeyCode.F10:          return 0x44;
                case KeyCode.F11:          return 0x57;
                case KeyCode.F12:          return 0x58;
                default:                   return 0;
            }
        }

        private static char GetSpecialChar(KeyCode kc)
        {
            switch (kc)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter: return '\n';
                case KeyCode.Backspace:   return '\b';
                case KeyCode.Tab:         return '\t';
                default:                  return '\0';
            }
        }

        public static readonly int[] LetterScancodes = new int[]
        {
            0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24,
            0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14,
            0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
        };

        private static int CharToOCScancode(char ch)
        {
            char lower = char.ToLower(ch);
            if (lower >= 'a' && lower <= 'z') return LetterScancodes[lower - 'a'];
            switch (ch)
            {
                case '1': return 0x02; case '2': return 0x03; case '3': return 0x04;
                case '4': return 0x05; case '5': return 0x06; case '6': return 0x07;
                case '7': return 0x08; case '8': return 0x09; case '9': return 0x0A;
                case '0': return 0x0B;
                case '-': case '_': return 0x0C;
                case '=': case '+': return 0x0D;
                case '[': case '{': return 0x1A;
                case ']': case '}': return 0x1B;
                case '\\': case '|': return 0x2B;
                case ';': case ':': return 0x27;
                case '\'': case '"': return 0x28;
                case '`': case '~': return 0x29;
                case ',': case '<': return 0x33;
                case '.': case '>': return 0x34;
                case '/': case '?': return 0x35;
                case ' ':           return 0x39;
                case '\n':          return 0x1C;
                case '\t':          return 0x0F;
                case '\b':          return 0x0E;
                default:            return 0x00;
            }
        }
    }

    /// <summary>
    /// Terminal screen window.
    /// FIX: removed C# blinking cursor (OpenOS draws its own cursor via gpu API).
    /// FIX: renderer now handles Unicode strings per cell (box-drawing, arrows, etc.)
    /// </summary>
    public class Dialog_ComputerScreen : Window
    {
        private readonly Comp_Computer comp;

        private const float BaseCellW = 8f;
        private const float BaseCellH = 15f;
        private const float ToolbarH  = 28f;
        private GUIStyle cellStyle;
        private GameObject _captureGo;

        public Dialog_ComputerScreen(Comp_Computer comp)
        {
            this.comp = comp;
            forcePause          = false;
            doCloseButton       = false;
            doCloseX            = false;
            resizeable          = true;
            draggable           = true;
            onlyOneOfTypeAllowed = true;
            closeOnAccept       = false;
            closeOnCancel       = false;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize
        {
            get
            {
                float w = comp.Screen.Width  * BaseCellW + 30f;
                float h = comp.Screen.Height * BaseCellH + ToolbarH + 34f;
                return new Vector2(
                    Mathf.Clamp(w, 400f, UI.screenWidth  - 40f),
                    Mathf.Clamp(h, 300f, UI.screenHeight - 60f));
            }
        }

        public override void PostOpen()
        {
            base.PostOpen();
            InputBuffer.Clear();
            _captureGo = new GameObject("RimCompInputCapture");
            _captureGo.AddComponent<ComputerInputCapture>();
            UnityEngine.Object.DontDestroyOnLoad(_captureGo);
            ComputerInputCapture.TargetWindow = this;
        }

        public override void PostClose()
        {
            ComputerInputCapture.TargetWindow = null;
            if (_captureGo != null)
                UnityEngine.Object.Destroy(_captureGo);
            base.PostClose();
        }

        private int _lastScreenW = -1;
        private int _lastScreenH = -1;

        public override void DoWindowContents(Rect inRect)
        {
            EnsureCellStyle();

            // Auto-resize window when GPU resolution changes.
            //
            // Also clamp the window's POSITION so the new rectangle stays
            // fully on-screen; otherwise a program that calls setResolution
            // to a larger mode (e.g. IcePlayer switching to 160×50) can push
            // the right/bottom edge off-screen, which looks like "only part
            // of the video is visible".
            var buf = comp.Screen;
            if (buf != null && (buf.Width != _lastScreenW || buf.Height != _lastScreenH))
            {
                _lastScreenW = buf.Width;
                _lastScreenH = buf.Height;
                float newW = buf.Width  * BaseCellW + 30f;
                float newH = buf.Height * BaseCellH + ToolbarH + 34f;
                newW = Mathf.Clamp(newW, 300f, UI.screenWidth  - 40f);
                newH = Mathf.Clamp(newH, 200f, UI.screenHeight - 60f);

                float newX = windowRect.x;
                float newY = windowRect.y;
                if (newX + newW > UI.screenWidth  - 20f) newX = UI.screenWidth  - 20f - newW;
                if (newY + newH > UI.screenHeight - 40f) newY = UI.screenHeight - 40f - newH;
                if (newX < 20f) newX = 20f;
                if (newY < 20f) newY = 20f;

                windowRect = new Rect(newX, newY, newW, newH);
                cellStyle = null; // force font size recalculation
                cellFont = null;  // force a fresh font at the new size
            }

            float y = inRect.y;
            DrawToolbar(inRect.x, y, inRect.width);
            y += ToolbarH;

            float screenH    = inRect.height - ToolbarH;
            var   screenRect = new Rect(inRect.x, y, inRect.width, screenH);
            Widgets.DrawBoxSolid(screenRect, Color.black);
            DrawScreen(screenRect);

            // NOTE: no DrawCursor here — OpenOS manages the cursor itself via
            // gpu.set() and component.invoke("gpu","cursorBlink",...).
            // Drawing a second cursor on top of OpenOS's own cursor was causing
            // a double-cursor artefact.
        }

        private void DrawToolbar(float x, float y, float w)
        {
            Widgets.DrawBoxSolid(new Rect(x, y, w, ToolbarH - 2f),
                new Color(0.12f, 0.12f, 0.12f));

            Text.Font   = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color   = new Color(0.6f, 1f, 0.6f);

            Widgets.Label(new Rect(x, y, w - 30f, ToolbarH),
                $"  {comp.parent.LabelCap}   [{comp.State}]   " +
                $"Lua {comp.LuaVerString}   " +
                $"GPU: {comp.Screen.Width}x{comp.Screen.Height}");

            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(x + w - 26f, y + 2f, 24f, 24f), "X"))
                Close();

            GUI.color   = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font   = GameFont.Small;
        }

        private Font    cellFont;
        private int     cellFontSize = 13;
        private bool    _fontRequested = false;

        // Reusable snapshot buffers (avoid per-frame GC). Resized as needed.
        private string[]  _snapChars;
        private Color32[] _snapFg;
        private Color32[] _snapBg;
        private int       _snapW, _snapH;

        private void DrawScreen(Rect area)
        {
            var buf = comp.Screen;
            if (buf == null) return;

            // ── Atomic buffer snapshot ──────────────────────────────────────
            // The Lua thread is constantly mutating chars/fg/bg. If we read
            // those arrays cell-by-cell during GUI.Label/DrawBoxSolid, a
            // single rendered frame can mix cells from two different Lua
            // frames. For IcePlayer (delta-encoded Bad Apple) this manifests
            // as "mostly white screen with thin black outlines" — the only
            // cells the renderer caught in the "black" half were the ones
            // that had just been written.
            //
            // We grab a consistent snapshot under buf.Lock once per render,
            // then iterate the snapshot without holding the lock while doing
            // Unity GUI calls.
            int W, H;
            lock (buf.Lock)
            {
                W = buf.Width;
                H = buf.Height;
                int total = W * H;
                if (_snapChars == null || _snapChars.Length < total)
                {
                    _snapChars = new string[total];
                    _snapFg    = new Color32[total];
                    _snapBg    = new Color32[total];
                }
                _snapW = W;
                _snapH = H;
                for (int row = 0; row < H; row++)
                {
                    for (int col = 0; col < W; col++)
                    {
                        int idx = row * W + col;
                        _snapChars[idx] = buf.GetChar(col, row);
                        _snapFg[idx]    = buf.GetFG(col, row);
                        _snapBg[idx]    = buf.GetBG(col, row);
                    }
                }
            }

            float cw = area.width  / W;
            float ch = area.height / H;

            EnsureCellStyle();

            // ── Pre-request ALL on-screen characters before rendering ──────────
            // Unity dynamic fonts populate glyphs asynchronously. We must call
            // RequestCharactersInTexture BEFORE GUI.Label, otherwise Unicode chars
            // (box-drawing ╔ ║ ═, arrows ← ↑ →, Cyrillic, etc.) show as ?.
            if (cellFont != null)
            {
                if (!_fontRequested)
                {
                    _fontRequested = true;
                    try
                    {
                        cellFont.RequestCharactersInTexture(
                            BoxDrawingPreload, cellFontSize, FontStyle.Normal);
                    }
                    catch { }
                }

                var sb = new System.Text.StringBuilder(W * H);
                int cells = W * H;
                for (int i = 0; i < cells; i++)
                {
                    string c = _snapChars[i];
                    if (!string.IsNullOrEmpty(c) && c != " ") sb.Append(c);
                }
                if (sb.Length > 0)
                {
                    try
                    {
                        cellFont.RequestCharactersInTexture(
                            sb.ToString(), cellFontSize, FontStyle.Normal);
                    }
                    catch { }
                }

                if (cellStyle != null) cellStyle.font = cellFont;
            }

            // ── Render cells from snapshot ────────────────────────────────────
            //
            // We ALWAYS draw the BG box for every cell, even when it's a space
            // on a black background. The previous "isEmpty" optimisation skipped
            // those cells, relying on the outer black rect underneath — but the
            // outer rect plus fractional cell sizes (cw = area.w / 160) meant
            // thin sub-pixel slivers of the black underlay bled through between
            // adjacent non-black cells, which showed up as the "black outlines
            // around characters" artefact in IcePlayer video playback.
            //
            // Overlap each cell by +1 px in both axes so adjacent cells meet
            // without any gap regardless of the fractional cell size.
            Text.Font   = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;

            for (int row = 0; row < H; row++)
            {
                for (int col = 0; col < W; col++)
                {
                    int idx = row * W + col;
                    Color32 bgColor = _snapBg[idx];
                    Color32 fgColor = _snapFg[idx];
                    string  cell    = _snapChars[idx];

                    float px = area.x + col * cw;
                    float py = area.y + row * ch;

                    Widgets.DrawBoxSolid(
                        new Rect(px, py, cw + 1f, ch + 1f),
                        new Color(bgColor.r / 255f, bgColor.g / 255f, bgColor.b / 255f));

                    if (string.IsNullOrEmpty(cell) || cell == " ") continue;

                    cellStyle.normal.textColor =
                        new Color(fgColor.r / 255f, fgColor.g / 255f, fgColor.b / 255f);

                    GUI.Label(new Rect(px, py, cw + 2f, ch + 1f), cell, cellStyle);
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
        }

        // Characters to pre-warm in the font atlas on first frame
        private static readonly string BoxDrawingPreload = BuildPreloadString();
        private static string BuildPreloadString()
        {
            var sb = new System.Text.StringBuilder();
            // ASCII printable
            for (int i = 0x20; i <= 0x7E; i++) sb.Append((char)i);
            // Box Drawing U+2500–U+257F
            for (int i = 0x2500; i <= 0x257F; i++) sb.Append(char.ConvertFromUtf32(i));
            // Block Elements U+2580–U+259F
            for (int i = 0x2580; i <= 0x259F; i++) sb.Append(char.ConvertFromUtf32(i));
            // Arrows U+2190–U+21FF
            for (int i = 0x2190; i <= 0x21FF; i++) sb.Append(char.ConvertFromUtf32(i));
            // Cyrillic U+0400–U+04FF
            for (int i = 0x0400; i <= 0x04FF; i++) sb.Append(char.ConvertFromUtf32(i));
            // Latin Extended U+00C0–U+024F
            for (int i = 0x00C0; i <= 0x024F; i++) sb.Append(char.ConvertFromUtf32(i));
            // Geometric shapes U+25A0–U+25FF
            for (int i = 0x25A0; i <= 0x25FF; i++) sb.Append(char.ConvertFromUtf32(i));
            return sb.ToString();
        }

        private void EnsureCellStyle()
        {
            if (cellStyle != null && cellFont != null) return;

            // Pick a font size that fits in BOTH dimensions so glyphs don't
            // overflow into adjacent cells. Earlier we sized purely by
            // height (`* 0.85`) which produced glyphs ~10 px wide in 8 px
            // cells at 160-column resolution — IcePlayer/MineOS users
            // reported "only ~60 columns visible" because adjacent letters
            // were drawing over each other.
            int sw = comp.Screen?.Width  ?? 80;
            int sh = comp.Screen?.Height ?? 25;
            float cellW = (windowRect.width  - 30f)              / Mathf.Max(1, sw);
            float cellH = (windowRect.height - ToolbarH - 34f)   / Mathf.Max(1, sh);
            // For a typical monospace font glyph_w ≈ 0.55 × fontSize
            // and line height ≈ 1.2 × fontSize. Cap fontSize so neither
            // dimension overflows the cell.
            float byWidth  = cellW / 0.55f;
            float byHeight = cellH * 0.83f;
            cellFontSize = Mathf.RoundToInt(
                Mathf.Clamp(Mathf.Min(byWidth, byHeight), 8f, 20f));

            // Unicode/Cyrillic problem:
            //
            //   `Font.CreateDynamicFontFromOSFont(string[] names, int)` in
            //   Unity does NOT build a glyph-by-glyph fallback chain — it
            //   just picks the first installed name from the list. If that
            //   font happens to miss a codepoint we'll still render '?'.
            //
            //   Instead we build a chain by hand:
            //     - Create a primary Font from a broad-coverage OS font.
            //     - Attach a list of fallback Fonts via Font.fallbackFonts
            //       so Unity's dynamic font atlas falls through to them
            //       when the primary doesn't contain a glyph (this IS the
            //       official supported mechanism, available since
            //       Unity 5.4).
            //
            //   We deliberately pick broad-Unicode fonts (not "Consolas"
            //   first). Consolas lacks many Unicode blocks, and because
            //   Unity's multi-name overload prefers the *first* installed
            //   match, putting Consolas first was causing the exact bug
            //   reported.
            if (cellFont == null)
            {
                // Order: broad Unicode monospace → broad Unicode proportional
                // → anything else that's likely to cover missing codepoints.
                string[] primaryCandidates =
                {
                    // Known-good broad Unicode monospace
                    "DejaVu Sans Mono", "Noto Sans Mono", "Cascadia Mono",
                    "Consolas", "Courier New", "Lucida Console",
                    "Liberation Mono", "Menlo", "Monaco",
                    // Broad Unicode proportional (not monospace, but ensures
                    // *something* renders)
                    "Segoe UI", "Arial Unicode MS", "Lucida Sans Unicode",
                    "Noto Sans", "DejaVu Sans", "Arial", "Tahoma",
                };

                foreach (var name in primaryCandidates)
                {
                    try
                    {
                        var f = Font.CreateDynamicFontFromOSFont(name, cellFontSize);
                        if (f != null) { cellFont = f; break; }
                    }
                    catch { }
                }

                // Very last resort: whatever Unity's dynamic default is.
                if (cellFont == null)
                {
                    try
                    {
                        cellFont = Font.CreateDynamicFontFromOSFont(
                            primaryCandidates, cellFontSize);
                    }
                    catch { }
                }

                // Build an explicit fallback chain. Any codepoint the primary
                // can't render triggers a lookup through these in order.
                if (cellFont != null)
                {
                    var fallbacks = new List<Font>();
                    // IMPORTANT: prefer MONOSPACE fallbacks. Earlier we listed
                    // Segoe UI / Tahoma / Arial first, but those are
                    // proportional — when a glyph fell through to them, its
                    // advance width didn't match the cell width and adjacent
                    // letters drifted out of column alignment (visible in the
                    // BSOD screenshot as "M i n e CS" instead of "MineCS").
                    // Cascadia/Consolas/DejaVu Sans Mono cover Cyrillic and
                    // box-drawing on most modern Windows installs; fall back
                    // to symbol-only proportional fonts only if all monospace
                    // candidates miss a codepoint.
                    string[] fallbackNames =
                    {
                        // Monospace, broad Unicode coverage (Cyrillic + boxes)
                        "Cascadia Mono", "Cascadia Code",
                        "Consolas", "Lucida Console",
                        "Courier New", "Liberation Mono",
                        "DejaVu Sans Mono", "Noto Sans Mono",
                        "Source Code Pro",
                        // Proportional last-resort (better wrong-width glyph
                        // than a "?" tofu)
                        "Segoe UI Symbol", "Arial Unicode MS",
                        "Lucida Sans Unicode", "Segoe UI",
                        "Tahoma", "Arial",
                    };
                    foreach (var name in fallbackNames)
                    {
                        // Don't double-add the primary
                        if (cellFont.name != null &&
                            cellFont.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        try
                        {
                            var fb = Font.CreateDynamicFontFromOSFont(name, cellFontSize);
                            if (fb != null) fallbacks.Add(fb);
                        }
                        catch { }
                    }
                    // Font.fallbackFonts is missing on the UnityEngine.dll
                    // shipped with this RimWorld build (CS1061 at compile
                    // time on some configurations), so set it via reflection.
                    // No-op if the property genuinely isn't there at runtime.
                    bool fallbackApplied = false;
                    try
                    {
                        var prop = typeof(Font).GetProperty(
                            "fallbackFonts",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance);
                        if (prop != null)
                        {
                            prop.SetValue(cellFont, fallbacks.ToArray(), null);
                            fallbackApplied = true;
                        }
                    }
                    catch { }

                    // One-time diagnostic: lets the user see in the host log
                    // which primary font Unity actually picked and how many
                    // fallbacks were attached. This is the "did monospace
                    // Cyrillic happen?" answer.
                    try
                    {
                        var fbNames = new List<string>();
                        foreach (var f in fallbacks)
                            if (f != null) fbNames.Add(f.name ?? "(unnamed)");
                        Verse.Log.Message(
                            $"[RimComputers] cell font primary='{cellFont.name}' " +
                            $"fontSize={cellFontSize} fallback({(fallbackApplied ? "ok" : "MISSING")})=" +
                            (fbNames.Count > 0 ? string.Join(", ", fbNames) : "<none>"));
                    }
                    catch { }
                }
            }

            cellStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap  = false,
                clipping  = TextClipping.Overflow,
                padding   = new RectOffset(0, 0, 0, 0),
                margin    = new RectOffset(0, 0, 0, 0),
                richText  = false,
                font      = cellFont,
                fontSize  = cellFontSize,
            };
            cellStyle.normal.textColor = Color.white;
            _fontRequested = false; // will trigger pre-warm on next Layout

            // Pre-warm the glyph atlas synchronously so the very first frame
            // doesn't render "?" for glyphs the font *does* have but hasn't
            // rasterised yet. Unity populates dynamic-font atlases lazily.
            if (cellFont != null)
            {
                try
                {
                    cellFont.RequestCharactersInTexture(
                        BoxDrawingPreload, cellFontSize, FontStyle.Normal);
                }
                catch { }
            }
        }
    }
}
