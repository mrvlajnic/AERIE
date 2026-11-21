using System.IO.Pipes;
using System.Windows;
using Search.Core;

namespace Search;

/// One window, one process: a second launch (a link clicked in another app
/// with Search as the default browser) hands its addresses to the first.
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // --data=folder: a whole world of its own. The single-instance name is
        // derived from it, so two profiles can run side by side and only the
        // same profile hands links over.
        foreach (var a in args) if (a.StartsWith("--data=")) Store.Folder = Path.GetFullPath(a[7..]);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Store.Folder)))[..10];
        var pipe = "SearchBrowser-" + Environment.UserName + "-" + key;
        using var single = new Mutex(true, @"Local\SearchBrowser-" + Environment.UserName + "-" + key, out bool first);
        if (!first)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
                client.Connect(3000);
                using var w = new StreamWriter(client);
                w.Write(string.Join("\n", args.Length == 0 ? new[] { "" } : args));
                return 0;
            }
            catch { /* the first one is gone; carry on as the first */ }
        }

        Directory.CreateDirectory(Store.Folder);
        Job.Hold();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                var log = Path.Combine(Store.Folder, "errors.log");
                // Bound the log: a failing paint loop must not fill the disk.
                if (File.Exists(log) && new FileInfo(log).Length > 1_000_000) File.Move(log, log + ".old", true);
                File.AppendAllText(log, $"{DateTime.Now:u} {e.Exception}\n\n");
            }
            catch { }
            e.Handled = true;
            try { (Application.Current?.MainWindow as BrowserWindow)?.Toast("Something went wrong — details in errors.log"); } catch { }
#if DEBUG
            System.Diagnostics.Debugger.Break();
#endif
        };
        var window = new BrowserWindow(args);
        Listen(window, pipe);
        return app.Run(window);
    }

    static void Listen(BrowserWindow window, string pipe)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    var lines = r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    window.Dispatcher.BeginInvoke(() => window.Arrived(lines));
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }
}
