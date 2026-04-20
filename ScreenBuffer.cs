using System;
using UnityEngine;

namespace RimComputers
{
    /// <summary>
    /// Text-mode screen buffer identical in concept to OC's GPU/Screen.
    /// Each cell holds a character, a foreground color and a background color.
    /// Default palette matches the OC Tier-1 8-color palette.
    /// </summary>
    public class ScreenBuffer
    {
        public readonly int Width;
        public readonly int Height;

        private char[,]    chars;
        private Color32[,] fg;
        private Color32[,] bg;

        private int cursorX = 0;
        private int cursorY = 0;

        // Current drawing colors
        public Color32 CurrentFG { get; set; } = new Color32(255, 255, 255, 255);
        public Color32 CurrentBG { get; set; } = new Color32(0,   0,   0,   255);

        // ── OC-style 16-color palette (BIOS default) ─────────────────────
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
            chars  = new char[width, height];
            fg     = new Color32[width, height];
            bg     = new Color32[width, height];
            Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        // Core operations  (map directly to OC gpu.* calls)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>gpu.set(x, y, text)</summary>
        public void Set(int x, int y, string text, bool vertical = false)
        {
            for (int i = 0; i < text.Length; i++)
            {
                int cx = vertical ? x     : x + i;
                int cy = vertical ? y + i : y;
                if (cx >= 0 && cx < Width && cy >= 0 && cy < Height)
                {
                    chars[cx, cy] = text[i];
                    fg[cx, cy]    = CurrentFG;
                    bg[cx, cy]    = CurrentBG;
                }
            }
        }

        /// <summary>gpu.fill(x, y, w, h, char)</summary>
        public void Fill(int x, int y, int w, int h, char c)
        {
            for (int cy = y; cy < y + h && cy < Height; cy++)
            for (int cx = x; cx < x + w && cx < Width;  cx++)
            {
                chars[cx, cy] = c;
                fg[cx, cy]    = CurrentFG;
                bg[cx, cy]    = CurrentBG;
            }
        }

        /// <summary>gpu.copy(x, y, w, h, tx, ty)</summary>
        public void Copy(int x, int y, int w, int h, int tx, int ty)
        {
            // Simple naive copy (no overlap handling yet)
            char[,]    tc = new char[w, h];
            Color32[,] tf = new Color32[w, h];
            Color32[,] tb = new Color32[w, h];

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

        /// <summary>gpu.get(x, y)</summary>
        public (char c, Color32 f, Color32 b) Get(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                return (' ', CurrentFG, CurrentBG);
            return (chars[x, y], fg[x, y], bg[x, y]);
        }

        /// <summary>Clear the whole screen.</summary>
        public void Clear()
        {
            for (int cy = 0; cy < Height; cy++)
            for (int cx = 0; cx < Width;  cx++)
            {
                chars[cx, cy] = ' ';
                fg[cx, cy]    = Palette[15]; // white
                bg[cx, cy]    = Palette[0];  // black
            }
            cursorX = 0;
            cursorY = 0;
        }

        // ════════════════════════════════════════════════════════════════════
        // Convenience: print with line wrapping + scrolling (for BIOS output)
        // ════════════════════════════════════════════════════════════════════

        public void Print(int x, int y, string text)
        {
            Set(x, y, text);
        }

        /// <summary>Append a line at the cursor, scrolling if needed.</summary>
        public void Println(string text)
        {
            if (cursorY >= Height)
            {
                ScrollUp(1);
                cursorY = Height - 1;
            }
            Set(cursorX, cursorY, text);
            cursorX  = 0;
            cursorY += 1;
        }

        public void ScrollUp(int lines)
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
                chars[cx, cy] = ' ';
                fg[cx, cy]    = CurrentFG;
                bg[cx, cy]    = CurrentBG;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Accessors for the renderer
        // ════════════════════════════════════════════════════════════════════

        public char    GetChar(int x, int y) => chars[x, y];
        public Color32 GetFG  (int x, int y) => fg[x, y];
        public Color32 GetBG  (int x, int y) => bg[x, y];
    }
}
