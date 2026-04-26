using System;
using UnityEngine;

namespace RimComputers
{
    /// <summary>
    /// Text-mode screen buffer identical in concept to OC's GPU/Screen.
    /// Each cell stores a Unicode string (supports box-drawing chars, arrows, etc.)
    /// Default palette matches the OC Tier-3 16-color palette.
    /// </summary>
    public class ScreenBuffer
    {
        public int Width  { get; private set; }
        public int Height { get; private set; }

        // Store full strings per cell so surrogate pairs and multi-char glyphs work
        private string[,] chars;
        private Color32[,] fg;
        private Color32[,] bg;

        // ── Render lock ──────────────────────────────────────────────────
        // The Lua thread mutates the buffer concurrently with the game
        // thread reading it from OnGUI. Without synchronisation a renderer
        // pass could capture cells from two different Lua frames mid-write
        // — this is what produced the IcePlayer "thin black outlines on a
        // mostly-white screen" artefact: Bad Apple's encoder uses delta
        // updates (set bg=white, fill old silhouette area; set bg=black,
        // fill new silhouette area), and our renderer was sampling between
        // the two halves so only the new edge cells ever appeared black.
        //
        // All mutating ops below take this lock; the renderer wraps its
        // read loop in `lock (buf.Lock) { ... }` to atomically capture one
        // consistent state.
        private readonly object _lock = new object();
        public object Lock => _lock;

        // Current drawing colors
        public Color32 CurrentFG { get; set; } = new Color32(255, 255, 255, 255);
        public Color32 CurrentBG { get; set; } = new Color32(0, 0, 0, 255);

        // ── OC-style 16-color palette ────────────────────────────────────
        public static readonly Color32[] Palette = new Color32[]
        {
            new Color32(0,   0,   0,   255), // 0 black
            new Color32(0,   0,  170,  255), // 1 dark blue
            new Color32(0,  170,  0,   255), // 2 dark green
            new Color32(0,  170, 170,  255), // 3 dark cyan
            new Color32(170,  0,  0,   255), // 4 dark red
            new Color32(170,  0, 170,  255), // 5 dark magenta
            new Color32(170, 170,  0,  255), // 6 gold
            new Color32(170, 170, 170, 255), // 7 light gray
            new Color32(85,  85,  85,  255), // 8 dark gray
            new Color32(85,  85, 255,  255), // 9 blue
            new Color32(85, 255,  85,  255), // 10 green
            new Color32(85, 255, 255,  255), // 11 cyan
            new Color32(255,  85,  85,  255),// 12 red
            new Color32(255,  85, 255,  255),// 13 magenta
            new Color32(255, 255,  85,  255),// 14 yellow
            new Color32(255, 255, 255,  255),// 15 white
        };

        public ScreenBuffer(int width, int height)
        {
            Width  = width;
            Height = height;
            chars  = new string[width, height];
            fg     = new Color32[width, height];
            bg     = new Color32[width, height];
            Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        // Resize — called by gpu.setResolution
        // ════════════════════════════════════════════════════════════════════

        public void Resize(int newWidth, int newHeight)
        {
            lock (_lock)
            {
                if (newWidth == Width && newHeight == Height) return;

                var newChars = new string[newWidth, newHeight];
                var newFg    = new Color32[newWidth, newHeight];
                var newBg    = new Color32[newWidth, newHeight];

                for (int cy = 0; cy < newHeight; cy++)
                    for (int cx = 0; cx < newWidth; cx++)
                    {
                        newChars[cx, cy] = " ";
                        newFg[cx, cy]    = Palette[15];
                        newBg[cx, cy]    = Palette[0];
                    }

                int copyW = Math.Min(Width, newWidth);
                int copyH = Math.Min(Height, newHeight);
                for (int cy = 0; cy < copyH; cy++)
                    for (int cx = 0; cx < copyW; cx++)
                    {
                        newChars[cx, cy] = chars[cx, cy];
                        newFg[cx, cy]    = fg[cx, cy];
                        newBg[cx, cy]    = bg[cx, cy];
                    }

                Width  = newWidth;
                Height = newHeight;
                chars  = newChars;
                fg     = newFg;
                bg     = newBg;

                CursorX = Math.Min(CursorX, Width  - 1);
                CursorY = Math.Min(CursorY, Height - 1);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Core operations  (map directly to OC gpu.* calls)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>gpu.set(x, y, text) — iterates Unicode code points.</summary>
        public void Set(int x, int y, string text, bool vertical = false)
        {
            lock (_lock)
            {
                int col = 0;
                int pos = 0;
                while (pos < text.Length)
                {
                    string cell;
                    if (char.IsHighSurrogate(text[pos]) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
                    {
                        cell = text.Substring(pos, 2);
                        pos += 2;
                    }
                    else
                    {
                        cell = text[pos].ToString();
                        pos++;
                    }

                    int cx = vertical ? x       : x + col;
                    int cy = vertical ? y + col : y;
                    if (cx >= 0 && cx < Width && cy >= 0 && cy < Height)
                    {
                        chars[cx, cy] = cell;
                        fg[cx, cy]    = CurrentFG;
                        bg[cx, cy]    = CurrentBG;
                    }
                    col++;
                }
            }
        }

        /// <summary>gpu.fill(x, y, w, h, char)</summary>
        public void Fill(int x, int y, int w, int h, char c)
        {
            lock (_lock)
            {
                string cs = c.ToString();
                for (int cy = y; cy < y + h && cy < Height; cy++)
                    for (int cx = x; cx < x + w && cx < Width; cx++)
                    {
                        chars[cx, cy] = cs;
                        fg[cx, cy]    = CurrentFG;
                        bg[cx, cy]    = CurrentBG;
                    }
            }
        }

        /// <summary>Fill with a Unicode string (for surrogate-pair characters).</summary>
        public void FillStr(int x, int y, int w, int h, string s)
        {
            lock (_lock)
            {
                for (int cy = y; cy < y + h && cy < Height; cy++)
                    for (int cx = x; cx < x + w && cx < Width; cx++)
                    {
                        chars[cx, cy] = s;
                        fg[cx, cy]    = CurrentFG;
                        bg[cx, cy]    = CurrentBG;
                    }
            }
        }

        /// <summary>gpu.copy(x, y, w, h, tx, ty)</summary>
        public void Copy(int x, int y, int w, int h, int tx, int ty)
        {
            lock (_lock)
            {
                var tc = new string[w, h];
                var tf = new Color32[w, h];
                var tb = new Color32[w, h];

                for (int dy = 0; dy < h; dy++)
                    for (int dx = 0; dx < w; dx++)
                    {
                        int sx = x + dx, sy = y + dy;
                        if (sx >= 0 && sx < Width && sy >= 0 && sy < Height)
                        {
                            tc[dx, dy] = chars[sx, sy];
                            tf[dx, dy] = fg[sx, sy];
                            tb[dx, dy] = bg[sx, sy];
                        }
                    }

                for (int dy = 0; dy < h; dy++)
                    for (int dx = 0; dx < w; dx++)
                    {
                        int dx2 = x + tx + dx, dy2 = y + ty + dy;
                        if (dx2 >= 0 && dx2 < Width && dy2 >= 0 && dy2 < Height)
                        {
                            chars[dx2, dy2] = tc[dx, dy];
                            fg[dx2, dy2]    = tf[dx, dy];
                            bg[dx2, dy2]    = tb[dx, dy];
                        }
                    }
            }
        }

        /// <summary>gpu.get(x, y)</summary>
        public (string c, Color32 f, Color32 b) Get(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                return (" ", CurrentFG, CurrentBG);
            return (chars[x, y] ?? " ", fg[x, y], bg[x, y]);
        }

        /// <summary>Clear the whole screen.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                for (int cy = 0; cy < Height; cy++)
                    for (int cx = 0; cx < Width; cx++)
                    {
                        chars[cx, cy] = " ";
                        fg[cx, cy]    = Palette[15];
                        bg[cx, cy]    = Palette[0];
                    }
                CursorX = 0;
                CursorY = 0;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Convenience
        // ════════════════════════════════════════════════════════════════════

        public void Print(int x, int y, string text) => Set(x, y, text);

        public void Println(string text)
        {
            if (CursorY >= Height) { ScrollUp(1); CursorY = Height - 1; }
            Set(CursorX, CursorY, text);
            CursorX = 0;
            CursorY += 1;
        }

        public void ScrollUp(int lines)
        {
            lock (_lock)
            {
                for (int cy = 0; cy < Height - lines; cy++)
                    for (int cx = 0; cx < Width; cx++)
                    {
                        chars[cx, cy] = chars[cx, cy + lines];
                        fg[cx, cy]    = fg[cx, cy + lines];
                        bg[cx, cy]    = bg[cx, cy + lines];
                    }
                for (int cy = Height - lines; cy < Height; cy++)
                    for (int cx = 0; cx < Width; cx++)
                    {
                        chars[cx, cy] = " ";
                        fg[cx, cy]    = CurrentFG;
                        bg[cx, cy]    = CurrentBG;
                    }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Accessors for the renderer
        // ════════════════════════════════════════════════════════════════════

        public string   GetChar(int x, int y) => chars[x, y] ?? " ";
        public Color32  GetFG(int x, int y)   => fg[x, y];
        public Color32  GetBG(int x, int y)   => bg[x, y];

        public int  CursorX       { get; set; } = 0;
        public int  CursorY       { get; set; } = 0;
        public bool CursorVisible { get; set; } = true;
    }
}
