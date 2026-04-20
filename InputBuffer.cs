using System;
using System.Collections.Generic;

namespace RimComputers
{
    /// <summary>
    /// Thread-safe input buffer shared between Unity UI thread (Dialog_ComputerScreen)
    /// and RimWorld tick thread (Comp_Computer / OCApi).
    /// </summary>
    public static class InputBuffer
    {
        private static readonly object _lock = new object();
        private static readonly Queue<(char ch, int scancode)> _keyDownQueue = new Queue<(char, int)>();
        private static readonly Queue<(char ch, int scancode)> _keyUpQueue = new Queue<(char, int)>();

        public static void EnqueueKeyDown(char ch, int scancode)
        {
            lock (_lock)
                _keyDownQueue.Enqueue((ch, scancode));
        }

        public static void EnqueueKeyUp(char ch, int scancode)
        {
            lock (_lock)
                _keyUpQueue.Enqueue((ch, scancode));
        }

        public static bool TryDequeueKeyDown(out char ch, out int scancode)
        {
            lock (_lock)
            {
                if (_keyDownQueue.Count > 0)
                {
                    var item = _keyDownQueue.Dequeue();
                    ch = item.ch;
                    scancode = item.scancode;
                    return true;
                }
            }
            ch = '\0';
            scancode = 0;
            return false;
        }

        public static bool TryDequeueKeyUp(out char ch, out int scancode)
        {
            lock (_lock)
            {
                if (_keyUpQueue.Count > 0)
                {
                    var item = _keyUpQueue.Dequeue();
                    ch = item.ch;
                    scancode = item.scancode;
                    return true;
                }
            }
            ch = '\0';
            scancode = 0;
            return false;
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _keyDownQueue.Clear();
                _keyUpQueue.Clear();
            }
        }
    }
}
