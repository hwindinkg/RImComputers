using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimComputers
{
    /// <summary>
    /// Keyboard input capture for the computer terminal.
    ///
    /// Uses OnGUI (Event system) instead of Update+inputString because:
    /// - Event.Use() prevents RimWorld from also processing the key
    /// - EventType.KeyDown fires for ALL keys including those Unity filters from inputString
    /// - Modifier state is available directly from Event.current.modifiers
    ///
    /// OC keyboard model: every key press sends key_down(addr, charCode, scancode, player)
    /// Modifier keys (Ctrl/Alt/Shift) also send their own key_down/key_up events so that
    /// keyboard.isControlDown() / isAltDown() / isShiftDown() work correctly in OpenOS.
    /// </summary>
    public class ComputerInputCapture : MonoBehaviour
    {
        public static Dialog_ComputerScreen TargetWindow { get; set; }

        // Modifier state — tracked for key_up detection
        private bool _lctrlDown = false;
        private bool _rctrlDown = false;
        private bool _laltDown = false;
        private bool _raltDown = false;
        private bool _lshiftDown = false;
        private bool _rshiftDown = false;

        // OC (DIK) scancodes for modifier keys
        private const int SC_LCONTROL = 0x1D;
        private const int SC_RCONTROL = 0x9D;
        private const int SC_LALT = 0x38;
        private const int SC_RALT = 0xB8;
        private const int SC_LSHIFT = 0x2A;
        private const int SC_RSHIFT = 0x36;

        public void OnGUI()
        {
            if (TargetWindow == null) return;

            var e = Event.current;
            if (e == null) return;

            // ── Modifier key_down / key_up ────────────────────────────────────
            // We track them in both KeyDown and KeyUp event types.
            if (e.type == EventType.KeyDown || e.type == EventType.KeyUp)
            {
                bool isDown = (e.type == EventType.KeyDown);
                TrackModifier(e.keyCode, KeyCode.LeftControl, ref _lctrlDown, SC_LCONTROL, isDown);
                TrackModifier(e.keyCode, KeyCode.RightControl, ref _rctrlDown, SC_RCONTROL, isDown);
                TrackModifier(e.keyCode, KeyCode.LeftAlt, ref _laltDown, SC_LALT, isDown);
                TrackModifier(e.keyCode, KeyCode.RightAlt, ref _raltDown, SC_RALT, isDown);
                TrackModifier(e.keyCode, KeyCode.LeftShift, ref _lshiftDown, SC_LSHIFT, isDown);
                TrackModifier(e.keyCode, KeyCode.RightShift, ref _rshiftDown, SC_RSHIFT, isDown);
            }

            if (e.type != EventType.KeyDown) return;

            bool ctrl = _lctrlDown || _rctrlDown;
            bool alt = _laltDown || _raltDown;

            KeyCode kc = e.keyCode;

            // ── Special / non-printable keys ─────────────────────────────────
            int specialSc = GetSpecialScancode(kc);
            if (specialSc != 0)
            {
                char ch = GetSpecialChar(kc);
                InputBuffer.EnqueueKeyDown(ch, specialSc);
                InputBuffer.EnqueueKeyUp(ch, specialSc);
                e.Use();
                return;
            }

            // ── Modifier-only keydown (already handled above) ─────────────────
            if (IsModifierKey(kc)) { e.Use(); return; }

            // ── Ctrl+letter ──────────────────────────────────────────────────
            // Send char=0, scancode=letter. OpenOS checks isControlDown()+code, not char.
            if (ctrl && kc >= KeyCode.A && kc <= KeyCode.Z)
            {
                int sc = LetterScancodes[kc - KeyCode.A];
                InputBuffer.EnqueueKeyDown('\0', sc);
                InputBuffer.EnqueueKeyUp('\0', sc);
                e.Use();
                return;
            }

            // ── Printable characters ─────────────────────────────────────────
            // e.character is the final composed character (handles shift, dead keys, etc.)
            char c = e.character;
            if (c != '\0' && c != '\b' && !ctrl && !alt)
            {
                // Skip ASCII control chars that Unity might inject
                if (c >= 32 || c == '\n' || c == '\t')
                {
                    int sc = CharToOCScancode(c);
                    InputBuffer.EnqueueKeyDown(c, sc);
                    InputBuffer.EnqueueKeyUp(c, sc);
                    e.Use();
                }
                return;
            }
        }

        private static void TrackModifier(KeyCode pressed, KeyCode target,
            ref bool wasDown, int scancode, bool isDown)
        {
            if (pressed != target) return;
            if (isDown && !wasDown)
                InputBuffer.EnqueueKeyDown('\0', scancode);
            else if (!isDown && wasDown)
                InputBuffer.EnqueueKeyUp('\0', scancode);
            wasDown = isDown;
        }

        private static bool IsModifierKey(KeyCode kc)
            => kc == KeyCode.LeftControl || kc == KeyCode.RightControl ||
               kc == KeyCode.LeftAlt || kc == KeyCode.RightAlt ||
               kc == KeyCode.LeftShift || kc == KeyCode.RightShift ||
               kc == KeyCode.LeftCommand || kc == KeyCode.RightCommand ||
               kc == KeyCode.LeftWindows || kc == KeyCode.RightWindows;

        private static int GetSpecialScancode(KeyCode kc)
        {
            switch (kc)
            {
                case KeyCode.Return: return 0x1C;
                case KeyCode.KeypadEnter: return 0x9C;
                case KeyCode.Backspace: return 0x0E;
                case KeyCode.Tab: return 0x0F;
                case KeyCode.Escape: return 0x01;
                case KeyCode.Delete: return 0xD3;
                case KeyCode.Insert: return 0xD2;
                case KeyCode.Home: return 0xC7;
                case KeyCode.End: return 0xCF;
                case KeyCode.PageUp: return 0xC9;
                case KeyCode.PageDown: return 0xD1;
                case KeyCode.UpArrow: return 0xC8;
                case KeyCode.DownArrow: return 0xD0;
                case KeyCode.LeftArrow: return 0xCB;
                case KeyCode.RightArrow: return 0xCD;
                case KeyCode.F1: return 0x3B;
                case KeyCode.F2: return 0x3C;
                case KeyCode.F3: return 0x3D;
                case KeyCode.F4: return 0x3E;
                case KeyCode.F5: return 0x3F;
                case KeyCode.F6: return 0x40;
                case KeyCode.F7: return 0x41;
                case KeyCode.F8: return 0x42;
                case KeyCode.F9: return 0x43;
                case KeyCode.F10: return 0x44;
                case KeyCode.F11: return 0x57;
                case KeyCode.F12: return 0x58;
                default: return 0;
            }
        }

        private static char GetSpecialChar(KeyCode kc)
        {
            switch (kc)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter: return '\n';
                case KeyCode.Backspace: return '\b';
                case KeyCode.Tab: return '\t';
                default: return '\0';
            }
        }

        // OC (DIK) scancodes for A-Z — matches full_keyboard.lua exactly
        public static readonly int[] LetterScancodes = new int[]
        {
            0x1E, // A
            0x30, // B
            0x2E, // C
            0x20, // D
            0x12, // E
            0x21, // F
            0x22, // G
            0x23, // H
            0x17, // I
            0x24, // J
            0x25, // K
            0x26, // L
            0x32, // M
            0x31, // N
            0x18, // O
            0x19, // P
            0x10, // Q
            0x13, // R
            0x1F, // S
            0x14, // T
            0x16, // U
            0x2F, // V
            0x11, // W
            0x2D, // X
            0x15, // Y
            0x2C, // Z
        };

        private static int CharToOCScancode(char ch)
        {
            char lower = char.ToLower(ch);
            if (lower >= 'a' && lower <= 'z') return LetterScancodes[lower - 'a'];
            switch (ch)
            {
                case '1': return 0x02;
                case '2': return 0x03;
                case '3': return 0x04;
                case '4': return 0x05;
                case '5': return 0x06;
                case '6': return 0x07;
                case '7': return 0x08;
                case '8': return 0x09;
                case '9': return 0x0A;
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
                case ' ': return 0x39;
                case '\n': return 0x1C;
                case '\t': return 0x0F;
                case '\b': return 0x0E;
                default: return 0x00;
            }
        }
    }

    /// <summary>
    /// Terminal screen window.
    /// </summary>
    public class Dialog_ComputerScreen : Window
    {
        private readonly Comp_Computer comp;

        private const float BaseCellW = 8f;
        private const float BaseCellH = 15f;
        private const float ToolbarH = 28f;
        private GUIStyle cellStyle;
        private GameObject _captureGo;

        public Dialog_ComputerScreen(Comp_Computer comp)
        {
            this.comp = comp;
            forcePause = false;
            doCloseButton = false;
            doCloseX = false;
            resizeable = true;
            draggable = true;
            onlyOneOfTypeAllowed = true;
            closeOnAccept = false;
            closeOnCancel = false;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize
        {
            get
            {
                float w = comp.Screen.Width * BaseCellW + 30f;
                float h = comp.Screen.Height * BaseCellH + ToolbarH + 34f;
                return new Vector2(
                    Mathf.Clamp(w, 400f, UI.screenWidth - 40f),
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

        public override void DoWindowContents(Rect inRect)
        {
            EnsureCellStyle();

            float y = inRect.y;
            DrawToolbar(inRect.x, y, inRect.width);
            y += ToolbarH;

            float screenH = inRect.height - ToolbarH;
            var screenRect = new Rect(inRect.x, y, inRect.width, screenH);
            Widgets.DrawBoxSolid(screenRect, Color.black);
            DrawScreen(screenRect);

            if (comp.State == ComputerState.Running)
                DrawCursor(screenRect);
        }

        private void DrawToolbar(float x, float y, float w)
        {
            Widgets.DrawBoxSolid(new Rect(x, y, w, ToolbarH - 2f),
                new Color(0.12f, 0.12f, 0.12f));

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(0.6f, 1f, 0.6f);

            Widgets.Label(new Rect(x, y, w - 30f, ToolbarH),
                $"  {comp.parent.LabelCap}   [{comp.State}]   " +
                $"Lua {comp.LuaVerString}   " +
                $"GPU: {comp.Props.screenWidth}x{comp.Props.screenHeight}");

            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(x + w - 26f, y + 2f, 24f, 24f), "X"))
                Close();

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        private void DrawScreen(Rect area)
        {
            var buf = comp.Screen;
            if (buf == null) return;

            float cw = area.width / buf.Width;
            float ch = area.height / buf.Height;

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;

            for (int row = 0; row < buf.Height; row++)
                for (int col = 0; col < buf.Width; col++)
                {
                    Color32 bg = buf.GetBG(col, row);
                    Color32 fg = buf.GetFG(col, row);
                    char c = buf.GetChar(col, row);

                    bool isEmpty = (c == ' ' || c == '\0') && bg.r == 0 && bg.g == 0 && bg.b == 0;
                    if (!isEmpty)
                        Widgets.DrawBoxSolid(
                            new Rect(area.x + col * cw, area.y + row * ch, cw + 0.5f, ch + 0.5f),
                            new Color(bg.r / 255f, bg.g / 255f, bg.b / 255f));

                    if (c == ' ' || c == '\0') continue;

                    cellStyle.normal.textColor =
                        new Color(fg.r / 255f, fg.g / 255f, fg.b / 255f);

                    GUI.Label(
                        new Rect(area.x + col * cw, area.y + row * ch, cw, ch + 1f),
                        c.ToString(), cellStyle);
                }

            Text.Anchor = TextAnchor.UpperLeft;
        }

        private float blinkTimer;
        private bool blinkOn = true;

        private void DrawCursor(Rect area)
        {
            blinkTimer += Time.deltaTime;
            if (blinkTimer > 0.5f) { blinkTimer = 0f; blinkOn = !blinkOn; }
            if (!blinkOn) return;

            float cw = area.width / comp.Screen.Width;
            float ch = area.height / comp.Screen.Height;
            int cy = comp.Screen.Height - 1;
            Widgets.DrawBoxSolid(
                new Rect(area.x, area.y + cy * ch, cw, ch),
                new Color(1f, 1f, 1f, 0.8f));
        }

        private void EnsureCellStyle()
        {
            if (cellStyle != null) return;
            cellStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = false,
                clipping = TextClipping.Overflow,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            cellStyle.normal.textColor = Color.white;
        }
    }
}