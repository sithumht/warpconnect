using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WarpConnect {
    static class Program {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateMutex(IntPtr lpMutexAttributes, bool bInitialOwner, string lpName);
        [DllImport("kernel32.dll")] static extern bool ReleaseMutex(IntPtr mutex);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("shell32.dll")] static extern int ShellExecute(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);
        [DllImport("shell32.dll")] static extern bool IsUserAnAdmin();
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

        const string MutexName = "WarpConnect.App.SingleInstance";
        const int SW_HIDE = 0;

        [STAThread]
        static int Main() {
            try {
                // Manifest already demands admin; double-check and relaunch if bypassed.
                if (!IsUserAnAdmin()) {
                    ShellExecute(IntPtr.Zero, "runas", Application.ExecutablePath, null, null, 1);
                    return 0;
                }

                bool createdNew;
                IntPtr mutex = CreateMutex(IntPtr.Zero, true, MutexName);
                createdNew = (mutex != IntPtr.Zero) && (Marshal.GetLastWin32Error() != 183); // ERROR_ALREADY_EXISTS
                if (!createdNew) {
                    IntPtr existing = FindWindow(null, "WARP Connect");
                    if (existing != IntPtr.Zero) ShowWindow(existing, 5); // SW_SHOW
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) {
                    try {
                        var log = Path.Combine(Path.GetTempPath(), "warpconnect-crash.log");
                        File.WriteAllText(log, "ThreadException: " + DateTime.Now + Environment.NewLine + e.Exception.ToString());
                    } catch { }
                };
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.Run(new MainForm());
                ReleaseMutex(mutex);
                return 0;
            } catch (Exception ex) {
                try {
                    var log = Path.Combine(Path.GetTempPath(), "warpconnect-crash.log");
                    File.WriteAllText(log, ex.ToString());
                } catch { }
                return 1;
            }
        }
    }
}
