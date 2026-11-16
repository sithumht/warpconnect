using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace WarpConnect {
    // Thin wrapper over the warpscout.exe engine process.
    //
    // The tunnel is a child process that reads "quit" from its piped stdin
    // (or exits when the pipe closes) so killing it never strands routes.
    public class Engine : IDisposable {
        public string ExePath { get; private set; }
        public string AccountPath { get; private set; }

        private Process _tunnel;
        private readonly Action<string> _log;

        public Engine(string accountPath, Action<string> log) {
            ExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "warpscout.exe");
            AccountPath = accountPath;
            _log = log ?? delegate { };
        }

        public bool IsAvailable { get { return File.Exists(ExePath); } }

        public class RunResult { public int ExitCode; public string StdOut; public string StdErr; }

        public RunResult RunCapture(IList<string> args, int timeoutMs) {
            var psi = BuildPsi(args);
            psi.RedirectStandardInput = false;
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using (var p = Process.Start(psi)) {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { stdout.AppendLine(e.Data); _log(e.Data); } };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { stderr.AppendLine(e.Data); _log(e.Data); } };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) {
                    try { p.Kill(); } catch { }
                    _log("(timed out after " + (timeoutMs / 1000) + "s)");
                    return new RunResult { ExitCode = -1, StdOut = stdout.ToString(), StdErr = stderr.ToString() };
                }
                p.WaitForExit();  // drain async handlers
                return new RunResult { ExitCode = p.ExitCode, StdOut = stdout.ToString(), StdErr = stderr.ToString() };
            }
        }

        // Run a bounded engine command and stream its output, line by line, to the log.
        public int Run(IList<string> args, int timeoutMs) {
            return RunCapture(args, timeoutMs).ExitCode;
        }

        // Start the tunnel process with piped stdin; returns the Process.
        public Process StartTunnel(string endpoint, string proto) {
            if (_tunnel != null && !_tunnel.HasExited) return _tunnel;
            var args = new List<string> { "tunnel", "-endpoint", endpoint, "-proto", proto ?? "awg", "-a", AccountPath };
            var psi = BuildPsi(args);
            psi.RedirectStandardInput = true;
            var p = Process.Start(psi);
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) _log("  " + e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) _log("  " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _tunnel = p;
            return p;
        }

        public void StopTunnel() {
            var p = _tunnel;
            if (p == null) return;
            try {
                if (!p.HasExited) {
                    p.StandardInput.WriteLine("quit");
                    p.StandardInput.Flush();
                }
            } catch (Exception e) {
                _log("stop write failed: " + e.Message);
            }
            if (!p.WaitForExit(12000)) {
                try { p.Kill(); } catch { }
            }
            _tunnel = null;
        }

        public bool TunnelExited { get { return _tunnel == null || _tunnel.HasExited; } }

        public void Dispose() { StopTunnel(); }

        private ProcessStartInfo BuildPsi(IList<string> args) {
            var joined = new StringBuilder();
            foreach (var a in args) {
                joined.Append(EscapeArg(a));
                joined.Append(' ');
            }
            return new ProcessStartInfo {
                FileName = ExePath,
                Arguments = joined.ToString().Trim(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        // Minimal argv escaping for our own (space-free) argument set.
        private static string EscapeArg(string a) {
            if (a == null || a.Length == 0) return "\"\"";
            if (a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0) return "\"" + a.Replace("\"", "\\\"") + "\"";
            return a;
        }
    }
}
