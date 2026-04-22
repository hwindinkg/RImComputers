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

            // Auto-resize window when GPU resolution changes
            var buf = comp.Screen;
            if (buf != null && (buf.Width != _lastScreenW || buf.Height != _lastScreenH))
            {
                _lastScreenW = buf.Width;
                _lastScreenH = buf.Height;
                float newW = buf.Width  * BaseCellW + 30f;
                float newH = buf.Height * BaseCellH + ToolbarH + 34f;
                newW = Mathf.Clamp(newW, 300f, UI.screenWidth  - 40f);
                newH = Mathf.Clamp(newH, 200f, UI.screenHeight - 60f);
                windowRect = new Rect(windowRect.x, windowRect.y, newW, newH);
                cellStyle = null; // force font size recalculation
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

        private void DrawScreen(Rect area)
        {
            var buf = comp.Screen;
            if (buf == null) return;

            float cw = area.width  / buf.Width;
            float ch = area.height / buf.Height;

            EnsureCellStyle();

            // ── Pre-request ALL on-screen characters before rendering ──────────
            // Unity dynamic fonts populate glyphs asynchronously. We must call
            // RequestCharactersInTexture BEFORE GUI.Label, otherwise Unicode chars
            // (box-drawing ╔ ║ ═, arrows ← ↑ →, Cyrillic, etc.) show as ?.
            // We request on EVERY Layout event so newly-appeared chars are covered.
            if (cellFont != null && Event.current.type == EventType.Layout)
            {
                var sb = new System.Text.StringBuilder(buf.Width * buf.Height);
                for (int row = 0; row < buf.Height; row++)
                    for (int col = 0; col < buf.Width; col++)
                    {
                        string c = buf.GetChar(col, row);
                        if (!string.IsNullOrEmpty(c) && c != " ") sb.Append(c);
                    }
                if (sb.Length > 0)
                    cellFont.RequestCharactersInTexture(sb.ToString(), cellFontSize, FontStyle.Normal);

                // Also request on first frame to pre-warm the font atlas
                if (!_fontRequested)
                {
                    _fontRequested = true;
                    cellFont.RequestCharactersInTexture(BoxDrawingPreload, cellFontSize, FontStyle.Normal);
                }

                // Rebuild style to pick up any atlas changes
                if (cellStyle != null) cellStyle.font = cellFont;
            }

            // ── Render cells ──────────────────────────────────────────────────
            Text.Font   = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;

            for (int row = 0; row < buf.Height; row++)
            {
                for (int col = 0; col < buf.Width; col++)
                {
                    Color32 bgColor = buf.GetBG(col, row);
                    Color32 fgColor = buf.GetFG(col, row);
                    string  cell    = buf.GetChar(col, row);

                    float px = area.x + col * cw;
                    float py = area.y + row * ch;

                    bool isEmpty = (cell == " " || cell == "\0" || string.IsNullOrEmpty(cell))
                                   && bgColor.r == 0 && bgColor.g == 0 && bgColor.b == 0;

                    if (!isEmpty)
                        Widgets.DrawBoxSolid(new Rect(px, py, cw + 0.5f, ch + 0.5f),
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

            cellFontSize = Mathf.RoundToInt(
                Mathf.Clamp(windowRect.height / (comp.Screen?.Height ?? 50) * 0.85f, 8f, 20f));

            // Build a single font with a fallback chain so characters missing
            // from the primary face (e.g. Cyrillic, box-drawing, arrows) are
            // rendered from the next-best OS font instead of showing as "?".
            //
            // Unity's `Font.CreateDynamicFontFromOSFont(string[] names, int)`
            // constructs a font backed by all named OS fonts — the first one
            // is primary, the rest are fallbacks. This is essential on
            // Windows where Consolas lacks some glyphs that "Segoe UI Symbol"
            // / "Lucida Sans Unicode" / "Arial Unicode MS" cover.
            if (cellFont == null)
            {
                string[] fontCandidates =
                {
                    // Monospace primaries
                    "Consolas", "Cascadia Mono", "Courier New", "Lucida Console",
                    "DejaVu Sans Mono", "Liberation Mono", "Menlo", "Monaco",
                    // Broad Unicode / box-drawing fallbacks
                    "Segoe UI Symbol", "Segoe UI", "Lucida Sans Unicode",
                    "Arial Unicode MS", "Noto Sans Mono", "Noto Sans",
                    "DejaVu Sans", "Arial",
                };

                try
                {
                    // Primary + fallbacks in one font object (Unity picks
                    // glyphs from the first font that has them).
                    cellFont = Font.CreateDynamicFontFromOSFont(
                        fontCandidates, cellFontSize);
                }
                catch { cellFont = null; }

                // Fallback: try them one by one if the multi-font overload
                // isn't available or returned null.
                if (cellFont == null)
                {
                    foreach (string fname in fontCandidates)
                    {
                        try
                        {
                            var f = Font.CreateDynamicFontFromOSFont(fname, cellFontSize);
                            if (f != null) { cellFont = f; break; }
                        }
                        catch { }
                    }
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
