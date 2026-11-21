using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace Search;

/// Search.exe --bench=out.json [--engine=webkit|webview2] [--data=folder]:
/// opens the same sites every time, and writes down what the whole browser —
/// this process and every process it started — holds in memory along the
/// way: empty, all tabs open, background tabs frozen, background tabs asleep.
/// CI runs it for both engines on the same machine.
sealed class Bench
{
    public static readonly string[] DefaultSites =
    {
        "https://en.wikipedia.org/wiki/Web_browser",
        "https://news.ycombinator.com/",
        "https://github.com/WebKit/WebKit",
        "https://developer.mozilla.org/en-US/docs/Web/JavaScript",
        "https://www.bbc.com/news",
        "https://stackoverflow.com/questions/11227809",
    };

    readonly BrowserWindow browser;
    readonly string output;
    readonly string[] sites;
    TaskCompletionSource? waiting;
    Tab? waitingFor;

    public Bench(BrowserWindow browser, string output, string[]? sites)
    {
        this.browser = browser;
        this.output = output;
        this.sites = sites is { Length: > 0 } ? sites : DefaultSites;
    }

    public static Bench? FromArgs(BrowserWindow browser)
    {
        string? output = null; string[]? sites = null;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("--bench=")) output = a[8..];
            if (a.StartsWith("--bench-sites=")) sites = a[14..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }
        return output == null ? null : new Bench(browser, output, sites);
    }

    public void Loaded(Tab t) { if (t == waitingFor) waiting?.TrySetResult(); }

    public async Task Run()
    {
        var samples = new List<object>();
        var loads = new List<object>();
        async Task Sample(string label)
        {
            Trace.Log("bench: sample " + label);
            await Task.Delay(1500);
            GC.Collect();
            var (priv, ws, n, names, each) = Measure();
            samples.Add(new { label, privateMB = priv / 1048576.0, workingSetMB = ws / 1048576.0, processes = n, names, byProcess = each });
        }

        await Task.Delay(3000);
        await Sample("empty window");
        foreach (var site in sites)
        {
            var clock = Stopwatch.StartNew();
            waiting = new TaskCompletionSource();
            Trace.Log("bench: open " + site);
            waitingFor = browser.Open(site);
            var finished = await Task.WhenAny(waiting.Task, Task.Delay(30000)) == waiting.Task;
            loads.Add(new { site, ms = clock.ElapsedMilliseconds, finished });
            await Task.Delay(2000);
        }
        await Task.Delay(4000);
        await Sample($"{sites.Length} tabs open");
        await Task.Delay(TimeSpan.FromSeconds(browser.Settings.FreezeSeconds + 15));
        await Sample("background tabs frozen");
        foreach (var t in browser.Space.All.Where(t => t != browser.Current).ToList()) browser.Sleep(t);
        await Task.Delay(5000);
        await Sample("background tabs asleep");

        var report = new
        {
            engine = browser.Engine.Name,
            version = browser.Engine.Version,
            lean = browser.Settings.Lean,
            sharedProcess = browser.Settings.SharedProcess,
            machine = Environment.MachineName,
            loads,
            samples,
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        browser.Close();
    }

    /// This process and all its descendants: commit charge and working set.
    static (long Private, long WorkingSet, int Count, List<string> Names, Dictionary<string, string> Each) Measure()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref e))
                do parents[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                while (Process32Next(snapshot, ref e));
        }
        finally { CloseHandle(snapshot); }

        // A parent id can belong to a process that has since exited and whose
        // number Windows gave to someone else: a child must start after its parent.
        var started = new Dictionary<int, DateTime?>();
        DateTime? Start(int pid)
        {
            if (started.TryGetValue(pid, out var t)) return t;
            try { using var p = Process.GetProcessById(pid); t = p.StartTime; } catch { t = null; }
            return started[pid] = t;
        }
        var mine = new HashSet<int> { Environment.ProcessId };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pid, parent) in parents)
            {
                if (mine.Contains(pid) || !mine.Contains(parent) || pid == parent) continue;
                var (child, mother) = (Start(pid), Start(parent));
                if (child == null || mother == null || child < mother) continue;
                mine.Add(pid); grew = true;
            }
        }
        long priv = 0, ws = 0; var names = new List<string>();
        var each = new Dictionary<string, (long Priv, long Ws)>();
        foreach (var pid in mine)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                priv += p.PrivateMemorySize64;
                ws += p.WorkingSet64;
                names.Add(p.ProcessName);
                each.TryGetValue(p.ProcessName, out var x);
                each[p.ProcessName] = (x.Priv + p.PrivateMemorySize64, x.Ws + p.WorkingSet64);
            }
            catch { }
        }
        return (priv, ws, names.Count, names.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList(),
            each.OrderByDescending(kv => kv.Value.Priv).ToDictionary(kv => kv.Key, kv => $"{kv.Value.Priv / 1048576.0:N0} MB private, {kv.Value.Ws / 1048576.0:N0} MB working set"));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
