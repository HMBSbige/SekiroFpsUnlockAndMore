using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SekiroFpsUnlockAndMore
{
    public partial class MainWindow
    {
        internal const string PROCESS_NAME = "sekiro";
        internal const string PROCESS_TITLE = "Sekiro";
        internal const string PROCESS_DESCRIPTION = "Shadows Die Twice";

        internal const string PATTERN_FRAMELOCK = "00 88 88 3C 4C 89 AB 00"; // ?? 88 88 3C 4C 89 AB ?? // pattern/signature of frame rate limiter, first byte (last in mem) can can be 88/90 instead of 89 due to precision loss on floating point numbers
        internal const string PATTERN_FRAMELOCK_MASK = "?xxxxxx?"; // mask for frame rate limiter signature scanning
        internal const string PATTERN_FRAMELOCK_LONG = "44 88 6B 00 C7 43 00 89 88 88 3C 4C 89 AB 00 00 00 00"; // 44 88 6B ?? C7 43 ?? 89 88 88 3C 4C 89 AB ?? ?? ?? ??
        internal const string PATTERN_FRAMELOCK_LONG_MASK = "xxx?xx?xxxxxxx????";
        internal const int PATTERN_FRAMELOCK_LONG_OFFSET = 7;
        internal const string PATTERN_FRAMELOCK_FUZZY = "C7 43 00 00 00 00 00 4C 89 AB 00 00 00 00";  // C7 43 ?? ?? ?? ?? ?? 4C 89 AB ?? ?? ?? ??
        internal const string PATTERN_FRAMELOCK_FUZZY_MASK = "xx?????xxx????";
        internal const int PATTERN_FRAMELOCK_FUZZY_OFFSET = 3; // offset to byte array from found position
        internal const string PATTERN_FRAMELOCK_RUNNING_FIX = "F3 0F 59 05 00 30 92 02 0F 2F F8"; // F3 0F 59 05 ?? 30 92 02 0F 2F F8 | 0F 51 C2 F3 0F 59 05 ?? ?? ?? ?? 0F 2F F8
        internal const string PATTERN_FRAMELOCK_RUNNING_FIX_MASK = "xxxx?xxxxxx";
        internal const int PATTERN_FRAMELOCK_RUNNING_FIX_OFFSET = 4;
        internal const string PATTERN_RESOLUTION = "80 07 00 00 38 04"; // 1920x1080
        internal const string PATTERN_RESOLUTION_MASK = "xxxxxx";
        internal const string PATTERN_WIDESCREEN_219 = "00 47 47 8B 94 C7 1C 02 00 00"; // ?? 47 47 8B 94 C7 1C 02 00 00
        internal const string PATTERN_WIDESCREEN_219_MASK = "?xxxxxxxxx";
        internal byte[] PATCH_FRAMERATE_RUNNING_FIX_DISABLE = { 0x90 };
        internal byte[] PATCH_FRAMERATE_UNLIMITED = { 0x00, 0x00, 0x00, 0x00 };
        internal byte[] PATCH_WIDESCREEN_219_DISABLE = { 0x74 };
        internal byte[] PATCH_WIDESCREEN_219_ENABLE = { 0xEB };
        internal byte[] PATCH_FOV_DISABLE = { 0x0C };

        // credits to jackfuste for FOV findings
        internal const string PATTERN_FOVSETTING = "F3 0F 10 08 F3 0F 59 0D 00 E7 9B 02"; // F3 0F 10 08 F3 0F 59 0D ?? E7 9B 02
        internal const string PATTERN_FOVSETTING_MASK = "xxxxxxxx?xxx";
        internal const int PATTERN_FOVSETTING_OFFSET = 8;
        internal Dictionary<byte, string> _fovMatrix = new Dictionary<byte, string>
        {
            { 0x10, "+ 15%" },
            { 0x14, "+ 40%" },
            { 0x18, "+ 75%" },
            { 0x1C, "+ 90%" },
        };

        internal long _offset_framelock = 0x0;
        internal long _offset_framelock_running_fix = 0x0;
        internal long _offset_resolution = 0x0;
        internal long _offset_widescreen_219 = 0x0;
        internal long _offset_fovsetting = 0x0;
        internal bool _running = false;
        internal Process _game;
        internal IntPtr _gameHwnd = IntPtr.Zero;
        internal IntPtr _gameProc = IntPtr.Zero;
        internal static IntPtr _gameProcStatic;
        internal readonly DispatcherTimer _dispatcherTimerCheck = new DispatcherTimer();
        internal string _logPath;
        internal bool _retryAccess = true;
        internal RECT _windowRect;
        internal RECT _clientRect;

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// On window loaded.
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadLanguage();

            _logPath = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location) + @"\SekiroFpsUnlockAndMore.log";

            cbSelectFov.ItemsSource = _fovMatrix;
            cbSelectFov.SelectedIndex = 0;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (!RegisterHotKey(hwnd, 9009, MOD_CONTROL, VK_P))
                MessageBox.Show(GetString(@"HotkeyFailed"), GetString(@"AppTitle"));

            // add a hook for WindowsMessageQueue to recognize hotkey-press
            ComponentDispatcher.ThreadFilterMessage += ComponentDispatcherThreadFilterMessage;

            _dispatcherTimerCheck.Tick += CheckGame;
            _dispatcherTimerCheck.Interval = new TimeSpan(0, 0, 0, 3);
            _dispatcherTimerCheck.Start();
        }

        /// <summary>
        /// On window closing.
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcherThreadFilterMessage;
            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, 9009);
            if (_gameProc != IntPtr.Zero)
                CloseHandle(_gameProc);
        }

        /// <summary>
        /// Windows Message queue (Wndproc) to catch HotKeyPressed
        /// </summary>
        private void ComponentDispatcherThreadFilterMessage(ref MSG msg, ref bool handled)
        {
            if (!handled)
            {
                if (msg.message == WM_HOTKEY_MSG_ID)    // hotkeyevent
                {
                    if (msg.wParam.ToInt32() == 9009)   // patch game
                    {
                        handled = true;
                        PatchGame();
                    }
                }
            }
        }

        /// <summary>
        /// Checks if game is running and initializes further functionality.
        /// </summary>
        private void CheckGame(object sender, EventArgs e)
        {
            var procList = Process.GetProcessesByName(PROCESS_NAME);
            if (procList.Length < 1)
                return;

            if (_running || _offset_framelock != 0x0)
                return;

            var gameIndex = -1;
            for (var i = 0; i < procList.Length; i++)
            {
                if (procList[i].MainWindowTitle == PROCESS_TITLE && procList[i].MainModule.FileVersionInfo.FileDescription.Contains(PROCESS_DESCRIPTION))
                {
                    gameIndex = i;
                    break;
                }
            }
            if (gameIndex < 0)
            {
                UpdateStatus(GetString(@"NoProcess"), Brushes.Red);
                LogToFile(GetString(@"NoProcess"));
                for (var j = 0; j < procList.Length; j++)
                {
                    LogToFile($@"\t{GetString(@"Proc")} #{j}: '{procList[j].MainModule.FileName}' | ({procList[j].MainModule.FileVersionInfo.FileName})");
                    LogToFile($@"\t{GetString(@"Description")} #{j}: {procList[j].MainWindowTitle} | {procList[j].MainModule.FileVersionInfo.CompanyName} | {procList[j].MainModule.FileVersionInfo.FileDescription}");
                    LogToFile($@"\t{GetString(@"Data")} #{j}: {procList[j].MainModule.FileVersionInfo.FileVersion} | {procList[j].MainModule.ModuleMemorySize} | {procList[j].StartTime} | {procList[j].Responding} | {procList[j].HasExited}");
                }
                return;
            }

            _game = procList[gameIndex];
            _gameHwnd = procList[gameIndex].MainWindowHandle;
            _gameProc = OpenProcess(PROCESS_ALL_ACCESS, false, (uint)procList[gameIndex].Id);
            _gameProcStatic = _gameProc;
            if (_gameHwnd == IntPtr.Zero || _gameProc == IntPtr.Zero || procList[gameIndex].MainModule.BaseAddress == IntPtr.Zero)
            {
                LogToFile(GetString(@"NoAccess"));
                LogToFile($@"{GetString(@"Hwnd")}: {_gameHwnd.ToString("X")}");
                LogToFile($@"{GetString(@"Proc")}: {_gameProc.ToString("X")}");
                LogToFile($@"{GetString(@"Base")}: {procList[gameIndex].MainModule.BaseAddress.ToString("X")}");
                if (!_retryAccess)
                {
                    UpdateStatus(GetString(@"NoAccess"), Brushes.Red);
                    _dispatcherTimerCheck.Stop();
                    return;
                }
                _gameHwnd = IntPtr.Zero;
                if (_gameProc != IntPtr.Zero)
                {
                    CloseHandle(_gameProc);
                    _gameProc = IntPtr.Zero;
                    _gameProcStatic = IntPtr.Zero;
                }
                LogToFile(GetString(@"Retrying"));
                _retryAccess = false;
                return;
            }

            //string gameFileVersion = FileVersionInfo.GetVersionInfo(procList[0].MainModule.FileName).FileVersion;

            _offset_framelock = PatternScan.FindPattern(_gameProc, procList[gameIndex].MainModule, PATTERN_FRAMELOCK, PATTERN_FRAMELOCK_MASK, ' ');
            Debug.WriteLine($@"1. Framelock found at: 0x{_offset_framelock:X}");
            if (!IsValid(_offset_framelock))
            {
                _offset_framelock = PatternScan.FindPattern(_gameProc, procList[gameIndex].MainModule, PATTERN_FRAMELOCK_FUZZY, PATTERN_FRAMELOCK_FUZZY_MASK, ' ') + PATTERN_FRAMELOCK_FUZZY_OFFSET;
                Debug.WriteLine($@"2. Framelock found at: 0x{_offset_framelock:X}");
            }
            if (!IsValid(_offset_framelock))
            {
                UpdateStatus(GetString(@"FrameLock404"), Brushes.Red);
                LogToFile(GetString(@"FrameLock404"));
                cbUnlockFps.IsEnabled = false;
                cbUnlockFps.IsChecked = false;
            }
            _offset_framelock_running_fix = PatternScan.FindPattern(_gameProc, procList[gameIndex].MainModule, PATTERN_FRAMELOCK_RUNNING_FIX, PATTERN_FRAMELOCK_RUNNING_FIX_MASK, ' ') + PATTERN_FRAMELOCK_RUNNING_FIX_OFFSET;
            Debug.WriteLine($@"Running fix found at: 0x{_offset_framelock_running_fix:X}");
            if (!IsValid(_offset_framelock_running_fix))
            {
                UpdateStatus(GetString(@"RunningFix404"), Brushes.Red);
                LogToFile(GetString(@"RunningFix404"));
                cbAddWidescreen.IsEnabled = false;
                cbAddWidescreen.IsChecked = false;
            }

            _offset_resolution = PatternScan.FindPattern(_gameProc, procList[gameIndex].MainModule, PATTERN_RESOLUTION, PATTERN_RESOLUTION_MASK, ' ');
            Debug.WriteLine($@"Resolution found at: 0x{_offset_resolution:X}");
            if (!IsValid(_offset_resolution))
            {
                UpdateStatus(GetString(@"Resolution404"), Brushes.Red);
                LogToFile(GetString(@"Resolution404"));
                cbAddWidescreen.IsEnabled = false;
                cbAddWidescreen.IsChecked = false;
            }
            _offset_widescreen_219 = PatternScan.FindPattern(_gameProc, procList[gameIndex].MainModule, PATTERN_WIDESCREEN_219, PATTERN_WIDESCREEN_219_MASK, ' ');
            Debug.WriteLine($@"Widescreen 21/9 found at: 0x{_offset_widescreen_219:X}");
            if (!IsValid(_offset_widescreen_219))
            {
                UpdateStatus(GetString(@"Widescreen404"), Brushes.Red);
                LogToFile(GetString(@"Widescreen404"));
                cbAddWidescreen.IsEnabled = false;
                cbAddWidescreen.IsChecked = false;
            }

            _offset_fovsetting = PatternScan.FindPattern(_gameProc, procList[gameIndex].MainModule, PATTERN_FOVSETTING, PATTERN_FOVSETTING_MASK, ' ') + PATTERN_FOVSETTING_OFFSET;
            Debug.WriteLine($@"FOV found at: 0x{_offset_fovsetting:X}");
            if (!IsValid(_offset_fovsetting))
            {
                UpdateStatus(GetString(@"Fov404"), Brushes.Red);
                LogToFile(GetString(@"Fov404"));
                cbFov.IsEnabled = false;
                cbFov.IsChecked = false;
            }

            GetWindowRect(_gameHwnd, out _windowRect);
            GetClientRect(_gameHwnd, out _clientRect);
            _running = true;
            _dispatcherTimerCheck.Stop();
            PatchGame();
        }

        /// <summary>
        /// Patch up this broken port
        /// </summary>
        private void PatchGame()
        {
            if (!_running)
                return;

            if (_game.HasExited)
            {
                _running = false;
                _gameHwnd = IntPtr.Zero;
                _gameProc = IntPtr.Zero;
                _gameProcStatic = IntPtr.Zero;
                _offset_framelock = 0x0;
                _offset_framelock_running_fix = 0x0;
                _offset_resolution = 0x0;
                _offset_widescreen_219 = 0x0;
                _offset_fovsetting = 0x0;
                UpdateStatus(GetString(@"WaitStatus"), Brushes.White);
                _dispatcherTimerCheck.Start();
                return;
            }

            if (cbUnlockFps.IsChecked == true)
            {
                var isNumber = int.TryParse(tbFps.Text, out var fps);
                if (fps < 0 || !isNumber)
                {
                    tbFps.Text = "60";
                    fps = 60;
                }
                else if (fps > 0 && fps < 30)
                {
                    tbFps.Text = "30";
                    fps = 30;
                }
                else if (fps > 300)
                {
                    tbFps.Text = "300";
                    fps = 300;
                }

                if (fps == 0)
                {
                    WriteBytes(_gameProcStatic, _offset_framelock, PATCH_FRAMERATE_UNLIMITED);
                    WriteBytes(_gameProcStatic, _offset_framelock_running_fix, new byte[] { 0xF8 }); // F8 is maximum
                }
                else
                {
                    var speed = 144 + (int)Math.Ceiling((fps - 60) / 16f) * 8; // calculation from game functions
                    if (speed > 248)
                        speed = 248;
                    var deltaTime = 1000f / fps / 1000f;
                    Debug.WriteLine("Deltatime hex: 0x" + getHexRepresentationFromFloat(deltaTime));
                    Debug.WriteLine("Speed hex: 0x" + speed.ToString("X"));
                    WriteBytes(_gameProcStatic, _offset_framelock, BitConverter.GetBytes(deltaTime));
                    WriteBytes(_gameProcStatic, _offset_framelock_running_fix, new[] { (byte)Convert.ToInt16(speed) });
                }
            }
            else if (cbUnlockFps.IsChecked == false)
            {
                var deltaTime = 1000f / 60 / 1000f;
                WriteBytes(_gameProcStatic, _offset_framelock, BitConverter.GetBytes(deltaTime));
                WriteBytes(_gameProcStatic, _offset_framelock_running_fix, PATCH_FRAMERATE_RUNNING_FIX_DISABLE);
            }

            if (cbAddWidescreen.IsChecked == true)
            {
                var isNumber = int.TryParse(tbWidth.Text, out var width);
                if (width < 800 || !isNumber)
                {
                    tbWidth.Text = "2560";
                    width = 2560;
                }
                else if (width > 5760)
                {
                    tbWidth.Text = "5760";
                    width = 5760;
                }

                isNumber = int.TryParse(tbHeight.Text, out var height);
                if (height < 450 || !isNumber)
                {
                    tbHeight.Text = "1080";
                    height = 1080;
                }
                else if (height > 2160)
                {
                    tbHeight.Text = "2160";
                    height = 2160;
                }
                WriteBytes(_gameProcStatic, _offset_resolution, BitConverter.GetBytes(width));
                WriteBytes(_gameProcStatic, _offset_resolution + 4, BitConverter.GetBytes(height));
                WriteBytes(_gameProcStatic, _offset_widescreen_219, (float)width / (float)height > 1.9f ? PATCH_WIDESCREEN_219_ENABLE : PATCH_WIDESCREEN_219_DISABLE);
            }
            else if (cbAddWidescreen.IsChecked == false)
            {
                WriteBytes(_gameProcStatic, _offset_resolution, BitConverter.GetBytes(1920));
                WriteBytes(_gameProcStatic, _offset_resolution + 4, BitConverter.GetBytes(1080));
                WriteBytes(_gameProcStatic, _offset_widescreen_219, PATCH_WIDESCREEN_219_DISABLE);
            }

            if (cbFov.IsChecked == true)
            {
                var fovByte = new byte[1];
                fovByte[0] = ((KeyValuePair<byte, string>)cbSelectFov.SelectedItem).Key;
                WriteBytes(_gameProcStatic, _offset_fovsetting, fovByte);
            }
            else if (cbFov.IsChecked == false)
            {
                WriteBytes(_gameProcStatic, _offset_fovsetting, PATCH_FOV_DISABLE);
            }

            if (cbBorderless.IsChecked == true)
            {
                if (!IsFullscreen(_gameHwnd))
                    SetWindowBorderless(_gameHwnd);
                else
                {
                    MessageBox.Show(GetString(@"BorderlessFailed"), GetString(@"AppTitle"));
                    cbBorderless.IsChecked = false;
                }
            }
            else if (cbBorderless.IsChecked == false && !IsFullscreen(_gameHwnd))
            {
                SetWindowWindowed(_gameHwnd);
            }

            if (cbUnlockFps.IsChecked == true || cbAddWidescreen.IsChecked == true || cbFov.IsChecked == true)
                UpdateStatus($@"{DateTime.Now} {GetString(@"GamePatched")}", Brushes.Green);
            else
                UpdateStatus($@"{DateTime.Now} {GetString(@"GameUnPatched")}", Brushes.White);
        }

        /// <summary>
        /// Returns the hexadecimal representation of an IEEE-754 floating point number
        /// </summary>
        /// <param name="input">The floating point number</param>
        /// <returns>The hexadecimal representation of the input</returns>
        private string getHexRepresentationFromFloat(float input)
        {
            var f = BitConverter.ToUInt32(BitConverter.GetBytes(input), 0);
            return $@"0x{f:X8}";
        }

        /// <summary>
        /// Checks if window is in fullscreen mode.
        /// </summary>
        /// <param name="hwnd">The main window handle of the window.</param>
        /// <remarks>
        /// Fullscreen windows have WS_EX_TOPMOST flag set.
        /// </remarks>
        /// <returns>True if window is run in fullscreen mode.</returns>
        private bool IsFullscreen(IntPtr hwnd)
        {
            var wndStyle = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            var wndExStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

            if (wndStyle == 0 || wndExStyle == 0)
                return false;

            if ((wndExStyle & WS_EX_TOPMOST) == 0)
                return false;
            if ((wndStyle & WS_POPUP) != 0)
                return false;
            if ((wndStyle & WS_CAPTION) != 0)
                return false;
            if ((wndStyle & WS_BORDER) != 0)
                return false;

            return true;
        }

        /// <summary>
        /// Sets a window to ordinary windowed mode
        /// </summary>
        /// <param name="hwnd">The handle to the window.</param>
        private void SetWindowWindowed(IntPtr hwnd)
        {
            var width = _windowRect.Right - _windowRect.Left;
            var height = _windowRect.Bottom - _windowRect.Top;
            Debug.WriteLine($@"Window Resolution: {width}x{height}");
            SetWindowLongPtr(hwnd, GWL_STYLE, WS_VISIBLE | WS_CAPTION | WS_BORDER | WS_CLIPSIBLINGS | WS_DLGFRAME | WS_SYSMENU | WS_GROUP | WS_MINIMIZEBOX);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 40, 40, width, height, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        /// <summary>
        /// Sets a window to borderless windowed mode and moves it to position 0x0.
        /// </summary>
        /// <param name="hwnd">The handle to the window.</param>
        private void SetWindowBorderless(IntPtr hwnd)
        {
            var width = _clientRect.Right - _clientRect.Left;
            var height = _clientRect.Bottom - _clientRect.Top;
            Debug.WriteLine($@"Client Resolution: {width}x{height}");
            SetWindowLongPtr(hwnd, GWL_STYLE, WS_VISIBLE | WS_POPUP);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, width, height, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        /// <summary>
        /// Checks if a pointer is valid.
        /// </summary>
        /// <param name="address">The address the pointer points to.</param>
        /// <returns>True if pointer points to a valid address.</returns>
        private static bool IsValid(long address)
        {
            return address >= 0x10000 && address < 0x000F000000000000;
        }

        /// <summary>
        /// Writes a given type and value to processes memory using a generic method.
        /// </summary>
        /// <param name="gameProc">The process handle to read from.</param>
        /// <param name="lpBaseAddress">The address to write from.</param>
        /// <param name="bytes">The byte array to write.</param>
        /// <returns>True if successful, false otherwise.</returns>
        private static bool WriteBytes(IntPtr gameProc, long lpBaseAddress, byte[] bytes)
        {
            return WriteProcessMemory(gameProc, lpBaseAddress, bytes, (ulong)bytes.Length, out _);
        }

        /// <summary>
        /// Check whether input is numeric only.
        /// </summary>
        /// <param name="text">The text to check.</param>
        /// <returns>True if inout is numeric only.</returns>
        private bool IsNumericInput(string text)
        {
            return Regex.IsMatch(text, @"[^0-9]+");
        }

        private void UpdateStatus(string text, Brush color)
        {
            tbStatus.Background = color;
            tbStatus.Text = text;
        }

        private void Numeric_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = IsNumericInput(e.Text);
        }

        private void Numeric_PastingHandler(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                var text = (string)e.DataObject.GetData(typeof(string));
                if (IsNumericInput(text)) e.CancelCommand();
            }
            else e.CancelCommand();
        }

        private void CheckBoxChanged_Handler(object sender, RoutedEventArgs e)
        {
            PatchGame();
        }

        private void BPatch_Click(object sender, RoutedEventArgs e)
        {
            PatchGame();
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }

        // log messages to file
        private void LogToFile(string msg)
        {
            var timedMsg = $@"[{DateTime.Now}] {msg}";
            Debug.WriteLine(timedMsg);
            try
            {
                using (var writer = new StreamWriter(_logPath, true))
                {
                    writer.WriteLine(timedMsg);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($@"{GetString(@"LogFileFail")}{ex.Message}", GetString(@"AppTitle"));
            }
        }

        #region i18n

        private void LoadLanguage()
        {
            var langName = CultureInfo.CurrentCulture.Name;
            if (langName != @"zh-CN")
            {
                if (Application.LoadComponent(new Uri(@"Resources/Langs/en-US.xaml", UriKind.Relative)) is
                        ResourceDictionary langRd)
                {
                    //如果已使用其他语言,先清空
                    if (Resources.MergedDictionaries.Count > 0)
                    {
                        Resources.MergedDictionaries.Clear();
                    }

                    Resources.MergedDictionaries.Add(langRd);
                }
            }
        }

        public static string GetString(string key)
        {
            var value = key;

            try
            {
                value = Application.Current.FindResource(key).ToString();
            }
            catch (Exception)
            {
                // ignored
            }

            return value;
        }

        #endregion

        #region WINAPI
        private const int WM_HOTKEY_MSG_ID = 0x0312;
        private const int MOD_CONTROL = 0x0002;
        private const uint VK_P = 0x0050;
        private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
        private const int GWL_EXSTYLE = -20;
        private const int GWL_STYLE = -16;
        private const uint WS_CLIPSIBLINGS = 0x04000000;
        private const uint WS_DLGFRAME = 0x00400000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_GROUP = 0x00020000;
        private const uint WS_MINIMIZEBOX = 0x00020000;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_BORDER = 0x00800000;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_WINDOWEDGE = 0x00000100;
        private const int HWND_NOTOPMOST = -2;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vlc);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        public static extern IntPtr SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;        // x position of upper-left corner
            public int Top;         // y position of upper-left corner
            public int Right;       // x position of lower-right corner
            public int Bottom;      // y position of lower-right corner
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteProcessMemory(
            IntPtr hProcess,
            long lpBaseAddress,
            [In, Out] byte[] lpBuffer,
            ulong dwSize,
            out IntPtr lpNumberOfBytesWritten);

        #endregion
    }
}
