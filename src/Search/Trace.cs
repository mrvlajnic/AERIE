namespace Search;

/// A line per step into trace.log, when asked for (--trace, or always in
/// --bench): what was happening when a native engine falls over without a word.
static class Trace
{
    static readonly bool on = Environment.GetCommandLineArgs().Any(a => a == "--trace" || a.StartsWith("--bench="));
    static readonly object gate = new();

    public static void Log(string what)
    {
        if (!on) return;
        lock (gate)
        {
            try { File.AppendAllText(Path.Combine(Core.Store.Folder, "trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {what}\n"); } catch { }
        }
    }
}
