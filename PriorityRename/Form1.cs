namespace PriorityRename;

public partial class Form1 : Form
{
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 0x5A;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_F2 = 0x71;

        private readonly NotifyIcon _trayIcon;
        private readonly ToolStripMenuItem _undoMenuItem;
        private readonly Stack<IReadOnlyList<RenameMapping>> _undoStack = new();
        private readonly Mutex _singleInstanceMutex;

        public Form1()
        {
            InitializeComponent();
            _singleInstanceMutex = new Mutex(true, @"Local\PriorityRename.CtrlF2");
            if (!_singleInstanceMutex.WaitOne(0, false))
            {
                _singleInstanceMutex.Dispose();
                Environment.Exit(0);
            }

            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Visible = false;
            Opacity = 0;

            _undoMenuItem = new ToolStripMenuItem("Undo Last Rename", null, (_, _) => UndoLastRename())
            {
                Enabled = false
            };
            var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Close());
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.AddRange([_undoMenuItem, exitItem]);

            _trayIcon = new NotifyIcon
            {
                Text = "Priority Rename",
                Icon = CreateTrayIcon(),
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => { };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL, VK_F2))
            {
                _trayIcon.ShowBalloonTip(
                    4000,
                    "Priority Rename",
                    "Ctrl+F2 is unavailable because another application owns this hotkey.",
                    ToolTipIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnregisterHotKey(Handle, HOTKEY_ID);
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
                BeginRenameFromExplorer();
            base.WndProc(ref m);
        }

        private void BeginRenameFromExplorer()
        {
            var explorerHandle = GetForegroundWindow();
            if (explorerHandle == nint.Zero)
                return;

            GetClassName(explorerHandle, out var className);
            if (!string.Equals(className, "CabinetWClass", StringComparison.Ordinal))
                return;

            if (!TryGetExplorerSelection(explorerHandle, out var sourcePath, out var name))
                return;

            using var dialog = new RenameDialog(name);
            dialog.StartPosition = FormStartPosition.CenterScreen;
            if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.TargetName))
                return;

            var result = RenameEngine.Rename(sourcePath, dialog.TargetName, dialog.TakePriority);
            if (!result.Success)
            {
                MessageBox.Show(this, result.Error ?? "The rename operation failed.", "Priority Rename",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (result.Mappings.Count > 0)
            {
                _undoStack.Push(result.Mappings);
                _undoMenuItem.Enabled = true;
            }
        }

        private void UndoLastRename()
        {
            if (_undoStack.Count == 0)
                return;
            var result = RenameEngine.Undo(_undoStack.Peek());
            if (!result.Success)
            {
                MessageBox.Show(this, result.Error ?? "Undo failed.", "Priority Rename",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _undoStack.Pop();
            _undoMenuItem.Enabled = _undoStack.Count > 0;
        }

        private static bool TryGetExplorerSelection(nint explorerHandle, out string sourcePath, out string name)
        {
            sourcePath = "";
            name = "";
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                    return false;
                dynamic shell = Activator.CreateInstance(shellType)!;

                foreach (dynamic window in shell.Windows())
                {
                    nint windowHandle = (nint)window.HWND;
                    if (windowHandle != explorerHandle)
                        continue;

                    dynamic document = window.Document;
                    dynamic selectedItems = document.SelectedItems();
                    int count = (int)selectedItems.Count;
                    if (count != 1)
                        return false;

                    dynamic item = selectedItems.Item(0);
                    sourcePath = (string)item.Path;
                    if (string.IsNullOrWhiteSpace(sourcePath))
                        return false;
                    if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                        return false;

                    name = Path.GetFileName(sourcePath);
                    return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static Icon CreateTrayIcon()
        {
            using var bitmap = new Bitmap(16, 16);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(38, 99, 235));
            using var pen = new Pen(Color.White, 1);
            graphics.DrawRectangle(pen, 3, 3, 9, 9);
            graphics.DrawLine(pen, 5, 7, 11, 7);
            graphics.DrawLine(pen, 8, 5, 8, 9);
            var handle = bitmap.GetHicon();
            return Icon.FromHandle(handle);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(nint hWnd, [System.Runtime.InteropServices.Out] System.Text.StringBuilder lpClassName, int nMaxCount);

        private static void GetClassName(nint hWnd, out string className)
        {
            var buffer = new System.Text.StringBuilder(256);
            GetClassName(hWnd, buffer, buffer.Capacity);
            className = buffer.ToString();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(nint hWnd, int id);
    }
