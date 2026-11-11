using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace UniMcp.Utils
{
    /// <summary>
    /// Windows 原生输入注入。
    ///
    /// 背景：旧的 simulate_* 实现只改写 Event.current 的字段，而 Event.current 是 IMGUI 的
    /// 当帧事件，在 Play 模式下既拿不到也不会被 Game View 消费，等于空实现。
    /// 这里改用 user32!SendInput 发送真实的系统输入事件（Unity 的 Input Manager 读的是
    /// GetAsyncKeyState / GetCursorPos，只认真实注入），从而同时驱动 Input 与 UGUI EventSystem。
    ///
    /// 坐标约定：对外 x/y 为 Game View 内容区像素坐标，左上角原点（与截图一致）。
    /// 内部通过一次自动标定求出内容区屏幕原点，再换算成系统屏幕坐标。
    /// </summary>
    public static class NativeInput
    {
        #region Native

        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint MAPVK_VK_TO_VSC = 0;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private const int SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        #endregion

        /// <summary>只有 Windows 编辑器可以注入系统输入。</summary>
        public static bool IsSupported
        {
            get { return Application.platform == RuntimePlatform.WindowsEditor; }
        }

        /// <summary>最近一次失败原因，供上层回传。</summary>
        public static string LastError = string.Empty;

        /// <summary>输入事件时间线，便于诊断按住时长（仅保留最近 40 条）。</summary>
        public static readonly List<string> Trace = new List<string>();

        private static void Log(string what)
        {
            if (Trace.Count > 40) Trace.RemoveAt(0);
            string state = "-";
            if (EditorApplication.isPlaying)
            {
                state = "focused=" + Application.isFocused;
            }
            Trace.Add(EditorApplication.timeSinceStartup.ToString("F3") + " " + what + " " + state);
        }

        private static Vector2 _origin;          // Game View 内容区左上角，屏幕坐标
        private static Vector2 _size;            // Game View 内容区尺寸
        private static bool _calibrated;
        private static Vector2 _probe;

        #region Game View 几何

        /// <summary>取 Game View 窗口矩形（屏幕坐标，含 Unity 自己的标题/工具条）。</summary>
        public static bool TryGetGameViewWindowRect(out Rect rect)
        {
            rect = default(Rect);
            try
            {
                Type t = Type.GetType("UnityEditor.GameView,UnityEditor");
                if (t == null) return false;
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(t);
                for (int i = 0; i < all.Length; i++)
                {
                    EditorWindow w = all[i] as EditorWindow;
                    if (w == null) continue;
                    rect = w.position;
                    return true;
                }
            }
            catch (Exception e)
            {
                LastError = "读取 Game View 矩形失败: " + e.Message;
            }
            return false;
        }

        /// <summary>已标定的内容区原点与尺寸；未标定时返回 false。</summary>
        public static bool TryGetContentRect(out Vector2 origin, out Vector2 size)
        {
            origin = _origin;
            size = _size;
            return _calibrated;
        }

        private static int ForegroundPidWindow()
        {
            try
            {
                return (int)Process.GetCurrentProcess().MainWindowHandle;
            }
            catch
            {
                return 0;
            }
        }

        private static EditorWindow GetGameViewWindow()
        {
            try
            {
                Type t = Type.GetType("UnityEditor.GameView,UnityEditor");
                if (t == null) return null;
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(t);
                for (int i = 0; i < all.Length; i++)
                {
                    EditorWindow w = all[i] as EditorWindow;
                    if (w != null) return w;
                }
            }
            catch
            {
            }
            return null;
        }

        private static void FocusEditor()
        {
            int h = ForegroundPidWindow();
            if (h != 0)
            {
                IntPtr hwnd = new IntPtr(h);
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }

            // 仅让编辑器前台还不够：Game View 必须成为当前焦点 view，
            // 否则滚轮等事件会被编辑器自己吞掉，游戏收不到。
            EditorWindow gv = GetGameViewWindow();
            if (gv != null)
            {
                try { gv.Focus(); } catch { }
            }
        }

        #endregion

        #region 分帧调度

        private struct Step
        {
            public Action Action;
            public float DelayAfter;
        }

        private static readonly Queue<Step> _steps = new Queue<Step>();
        private static bool _hooked;
        private static double _blockedUntil;

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            EditorApplication.update += Tick;
        }

        private static void Enqueue(Action step)
        {
            _steps.Enqueue(new Step { Action = step, DelayAfter = 0f });
            Hook();
        }

        /// <summary>
        /// 在「上一动作执行完成」之后等待若干秒。
        /// 注意不能按入队时刻计算：Editor 的 update 回调会被编辑器主线程上的
        /// 其它工作（编译、MCP 请求）阻塞，入队时的绝对时间往往在执行前就已过期。
        /// </summary>
        private static void EnqueueDelay(float seconds)
        {
            if (seconds <= 0f) return;
            _steps.Enqueue(new Step { Action = null, DelayAfter = seconds });
            Hook();
        }

        private static void Tick()
        {
            if (_steps.Count == 0)
            {
                _hooked = false;
                EditorApplication.update -= Tick;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now < _blockedUntil) return;

            Step s = _steps.Dequeue();
            if (s.Action != null)
            {
                try
                {
                    s.Action();
                }
                catch (Exception e)
                {
                    LastError = "输入注入步骤异常: " + e.Message;
                    UnityEngine.Debug.LogError("[NativeInput] " + e);
                }
            }

            // 每步至少隔 20ms，确保 Unity 能采到一次完整的按下状态
            _blockedUntil = now + Mathf.Max(s.DelayAfter, 0.02f);
        }

        #endregion

        #region 标定

        /// <summary>
        /// 自动标定 Game View 内容区在屏幕上的原点。
        /// 做法：把光标放到窗口中心并读回 Input.mousePosition，
        /// 由 origin = cursor - mousePosition 反解（y 需按左下原点翻转）。
        /// </summary>
        private static void EnsureCalibrated(Action onReady)
        {
            // Game View 尺寸/布局变化后旧标定失效
            if (_calibrated && Screen.width > 0 &&
                (Mathf.Abs(_size.x - Screen.width) > 1f || Mathf.Abs(_size.y - Screen.height) > 1f))
            {
                _calibrated = false;
            }

            if (_calibrated)
            {
                if (onReady != null) onReady();
                return;
            }

            if (EditorApplication.isPlaying)
            {
                Rect r;
                if (TryGetGameViewWindowRect(out r))
                {
                    int px = Mathf.RoundToInt(r.x + r.width * 0.5f);
                    int py = Mathf.RoundToInt(r.y + r.height * 0.5f);
                    Enqueue(delegate
                    {
                        FocusEditor();
                        SetCursorPos(px, py);
                        _probe = new Vector2(px, py);
                    });
                    // Unity 的 Input.mousePosition 在 PlayerLoop 里刷新，
                    // SetCursorPos 之后必须空转几帧才能读到新值。
                    Enqueue(null);
                    Enqueue(null);
                    Enqueue(null);
                    Enqueue(delegate
                    {
                        Vector2 mp = Input.mousePosition;
                        float h = Screen.height;
                        float ox = _probe.x - mp.x;
                        float oy = _probe.y + mp.y - h;
                        // 合理性检查：原点应落在窗口左上角附近
                        bool ok = Mathf.Abs(ox - r.x) < 60f && Mathf.Abs(oy - r.y) < 120f;
                        if (ok)
                        {
                            _origin = new Vector2(ox, oy);
                            _size = new Vector2(Screen.width, Screen.height);
                            _calibrated = true;
                        }
                        else
                        {
                            LastError = "标定结果异常 (origin=" + ox.ToString("F1") + "," + oy.ToString("F1") + ")，回退估算值";
                            UseEstimatedGeometry(r);
                        }
                        if (onReady != null) onReady();
                    });
                    return;
                }
            }

            Rect rr;
            if (TryGetGameViewWindowRect(out rr)) UseEstimatedGeometry(rr);
            if (onReady != null) onReady();
        }

        /// <summary>非 Play 模式或标定失败时的估算：Unity 自己的标题栏+工具条约 40px。</summary>
        private static void UseEstimatedGeometry(Rect windowRect)
        {
            _origin = new Vector2(windowRect.x + 2f, windowRect.y + 40f);
            // 内容尺寸以 Screen 为准（编辑器里 Screen 就是 Game View 尺寸），
            // 比用窗口高度减去标题栏更可靠。
            float w = Screen.width > 0 ? Screen.width : Mathf.Max(windowRect.width - 4f, 1f);
            float h = Screen.height > 0 ? Screen.height : Mathf.Max(windowRect.height - 40f, 1f);
            _size = new Vector2(w, h);
            _calibrated = true;
        }

        /// <summary>丢弃标定结果（例如 Game View 尺寸或布局变化后）。</summary>
        public static void InvalidateCalibration()
        {
            _calibrated = false;
        }

        #endregion

        #region 底层发送

        private static bool SendMouse(uint flags, int data)
        {
            INPUT inp = new INPUT();
            inp.type = INPUT_MOUSE;
            inp.u.mi.dwFlags = flags;
            inp.u.mi.mouseData = unchecked((uint)data);
            INPUT[] arr = new INPUT[] { inp };
            uint sent = SendInput(1, arr, Marshal.SizeOf(typeof(INPUT)));
            if (sent == 0)
            {
                LastError = "SendInput(mouse) 失败，错误码 " + Marshal.GetLastWin32Error();
                return false;
            }
            return true;
        }

        private static bool SendKey(uint vk, bool down)
        {
            // 同时提供 wVk 与扫描码：Unity 旧 Input Manager 轮询 GetAsyncKeyState(VK)，
            // 用 VK 事件能可靠地反映按下/抬起；扫描码一并给出以兼容 Raw Input 路径。
            INPUT inp = new INPUT();
            inp.type = INPUT_KEYBOARD;
            inp.u.ki.wVk = (ushort)vk;
            inp.u.ki.wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            inp.u.ki.dwFlags = down ? 0u : KEYEVENTF_KEYUP;
            INPUT[] arr = new INPUT[] { inp };
            uint sent = SendInput(1, arr, Marshal.SizeOf(typeof(INPUT)));
            if (sent == 0)
            {
                LastError = "SendInput(key) 失败，错误码 " + Marshal.GetLastWin32Error();
                return false;
            }
            Log("key vk=" + vk + " " + (down ? "DOWN" : "UP"));
            return true;
        }

        private static void MoveToScreen(float screenX, float screenY)
        {
            int sx = Mathf.RoundToInt(screenX);
            int sy = Mathf.RoundToInt(screenY);
            SetCursorPos(sx, sy);

            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = Mathf.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 1);
            int vh = Mathf.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 1);

            INPUT inp = new INPUT();
            inp.type = INPUT_MOUSE;
            inp.u.mi.dx = Mathf.RoundToInt((sx - vx) * 65535f / Mathf.Max(vw - 1, 1));
            inp.u.mi.dy = Mathf.RoundToInt((sy - vy) * 65535f / Mathf.Max(vh - 1, 1));
            inp.u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            INPUT[] arr = new INPUT[] { inp };
            SendInput(1, arr, Marshal.SizeOf(typeof(INPUT)));
        }

        private static uint ButtonDownFlag(int button)
        {
            if (button == 1) return MOUSEEVENTF_RIGHTDOWN;
            if (button == 2) return MOUSEEVENTF_MIDDLEDOWN;
            return MOUSEEVENTF_LEFTDOWN;
        }

        private static uint ButtonUpFlag(int button)
        {
            if (button == 1) return MOUSEEVENTF_RIGHTUP;
            if (button == 2) return MOUSEEVENTF_MIDDLEUP;
            return MOUSEEVENTF_LEFTUP;
        }

        #endregion

        #region 对外 API

        /// <summary>Game View 内容区坐标（左上原点）换算到屏幕坐标。</summary>
        public static Vector2 GameViewToScreen(float x, float y)
        {
            return new Vector2(_origin.x + x, _origin.y + y);
        }

        /// <summary>点击：移动 → 按下 → 松开，跨帧执行。</summary>
        public static void Click(float x, float y, int button, float delaySeconds)
        {
            if (!IsSupported)
            {
                LastError = "当前平台不支持原生输入注入（仅 Windows 编辑器）";
                return;
            }
            EnqueueDelay(delaySeconds);
            EnsureCalibrated(delegate
            {
                Vector2 s = GameViewToScreen(x, y);
                Enqueue(delegate { FocusEditor(); MoveToScreen(s.x, s.y); });
                Enqueue(delegate { SendMouse(ButtonDownFlag(button), 0); });
                Enqueue(delegate { SendMouse(ButtonUpFlag(button), 0); });
            });
        }

        /// <summary>拖拽：按下 → 逐帧移动到目标 → 松开。</summary>
        public static void Drag(float x, float y, float toX, float toY, float durationSeconds, float delaySeconds)
        {
            if (!IsSupported)
            {
                LastError = "当前平台不支持原生输入注入（仅 Windows 编辑器）";
                return;
            }
            EnqueueDelay(delaySeconds);
            EnsureCalibrated(delegate
            {
                Vector2 a = GameViewToScreen(x, y);
                Vector2 b = GameViewToScreen(toX, toY);
                int steps = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(durationSeconds, 0.05f) * 60f), 4, 240);

                Enqueue(delegate { FocusEditor(); MoveToScreen(a.x, a.y); });
                Enqueue(delegate { SendMouse(MOUSEEVENTF_LEFTDOWN, 0); });
                for (int i = 1; i <= steps; i++)
                {
                    float t = (float)i / steps;
                    Vector2 p = Vector2.Lerp(a, b, t);
                    Enqueue(delegate { MoveToScreen(p.x, p.y); });
                }
                Enqueue(delegate { SendMouse(MOUSEEVENTF_LEFTUP, 0); });
            });
        }

        /// <summary>按键：按下 → 保持 hold 秒 → 抬起。</summary>
        public static void Key(string keyCode, float holdSeconds, float delaySeconds)
        {
            if (!IsSupported)
            {
                LastError = "当前平台不支持原生输入注入（仅 Windows 编辑器）";
                return;
            }
            uint vk;
            if (!TryMapKeyCode(keyCode, out vk))
            {
                LastError = "无法识别的按键: " + keyCode;
                return;
            }
            EnqueueDelay(delaySeconds);
            Enqueue(delegate { FocusEditor(); });
            Enqueue(delegate { SendKey(vk, true); });
            EnqueueDelay(Mathf.Max(holdSeconds, 0.02f));
            Enqueue(null);   // 至少隔一帧，保证 Unity 能采到按下状态
            Enqueue(delegate { SendKey(vk, false); });

            // 兜底：若抬起未被识别（按键卡住会让游戏镜头一直移动），再补发一次。
            EnqueueDelay(0.06f);
            Enqueue(delegate
            {
                if (!EditorApplication.isPlaying) return;
                KeyCode kc;
                if (Enum.TryParse<KeyCode>(keyCode, true, out kc) && Input.GetKey(kc))
                {
                    SendKey(vk, false);
                    LastError = "按键 " + keyCode + " 首次抬起未被识别，已补发 keyup";
                }
            });
        }

        /// <summary>滚轮：先在目标位置落点再滚动。</summary>
        public static void Scroll(float x, float y, float delta, float delaySeconds)
        {
            if (!IsSupported)
            {
                LastError = "当前平台不支持原生输入注入（仅 Windows 编辑器）";
                return;
            }
            EnqueueDelay(delaySeconds);
            EnsureCalibrated(delegate
            {
                Vector2 s = GameViewToScreen(x, y);
                Enqueue(delegate { FocusEditor(); MoveToScreen(s.x, s.y); });
                Enqueue(delegate { SendMouse(MOUSEEVENTF_WHEEL, Mathf.RoundToInt(delta * 120f)); });
            });
        }

        #endregion

        #region KeyCode → VK

        private static readonly Dictionary<KeyCode, uint> VkMap = BuildVkMap();

        private static Dictionary<KeyCode, uint> BuildVkMap()
        {
            Dictionary<KeyCode, uint> m = new Dictionary<KeyCode, uint>();
            for (char c = 'A'; c <= 'Z'; c++) m[(KeyCode)(int)c] = (uint)c;
            for (char c = '0'; c <= '9'; c++) m[(KeyCode)(int)c] = (uint)c;
            m[KeyCode.Space] = 0x20;
            m[KeyCode.Escape] = 0x1B;
            m[KeyCode.Return] = 0x0D;
            m[KeyCode.KeypadEnter] = 0x0D;
            m[KeyCode.Tab] = 0x09;
            m[KeyCode.Backspace] = 0x08;
            m[KeyCode.Delete] = 0x2E;
            m[KeyCode.Insert] = 0x2D;
            m[KeyCode.Home] = 0x24;
            m[KeyCode.End] = 0x23;
            m[KeyCode.PageUp] = 0x21;
            m[KeyCode.PageDown] = 0x22;
            m[KeyCode.UpArrow] = 0x26;
            m[KeyCode.DownArrow] = 0x28;
            m[KeyCode.LeftArrow] = 0x25;
            m[KeyCode.RightArrow] = 0x27;
            m[KeyCode.LeftShift] = 0x10;
            m[KeyCode.RightShift] = 0x10;
            m[KeyCode.LeftControl] = 0x11;
            m[KeyCode.RightControl] = 0x11;
            m[KeyCode.LeftAlt] = 0x12;
            m[KeyCode.RightAlt] = 0x12;
            m[KeyCode.LeftCommand] = 0x5B;
            m[KeyCode.Menu] = 0x5D;
            m[KeyCode.CapsLock] = 0x14;
            m[KeyCode.Pause] = 0x13;
            m[KeyCode.Minus] = 0xBD;
            m[KeyCode.Equals] = 0xBB;
            m[KeyCode.Comma] = 0xBC;
            m[KeyCode.Period] = 0xBE;
            m[KeyCode.Slash] = 0xBF;
            m[KeyCode.Semicolon] = 0xBA;
            m[KeyCode.Quote] = 0xDE;
            m[KeyCode.LeftBracket] = 0xDB;
            m[KeyCode.RightBracket] = 0xDD;
            m[KeyCode.Backslash] = 0xDC;
            m[KeyCode.BackQuote] = 0xC0;
            for (int i = 0; i < 12; i++) m[KeyCode.F1 + i] = (uint)(0x70 + i);
            return m;
        }

        /// <summary>把 KeyCode 字符串（或 "A"/"Space"/"Alpha1" 等写法）映射为 Windows 虚拟键码。</summary>
        public static bool TryMapKeyCode(string keyCode, out uint vk)
        {
            vk = 0;
            if (string.IsNullOrEmpty(keyCode)) return false;

            string s = keyCode.Trim();
            if (s.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase) && s.Length == 7)
            {
                char d = s[6];
                if (d >= '0' && d <= '9') { vk = (uint)(0x60 + (d - '0')); return true; }
            }
            if (s.Length >= 6 && s.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase))
            {
                char d = s[5];
                if (d >= '0' && d <= '9') { vk = (uint)d; return true; }
            }

            KeyCode kc;
            if (Enum.TryParse<KeyCode>(s, true, out kc))
            {
                uint found;
                if (VkMap.TryGetValue(kc, out found))
                {
                    vk = found;
                    return true;
                }
            }

            if (s.Length == 1)
            {
                char c = char.ToUpperInvariant(s[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = (uint)c; return true; }
            }
            return false;
        }

        #endregion
    }
}
