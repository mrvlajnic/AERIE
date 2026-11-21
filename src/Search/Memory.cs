using System.Runtime.InteropServices;

namespace Search;

/// Windows tells us when the machine is short of memory. Background pages are
/// the first thing worth giving back, so the browser sleeps the least recently
/// used ones instead of waiting for the whole machine to thrash.
static class Memory
{
    public static event Action? Low;
    static int started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        var thread = new Thread(Loop) { IsBackground = true, Name = "memory-pressure" };
        thread.Start();
    }

    static void Loop()
    {
        var handle = CreateMemoryResourceNotification(0); // LowMemoryResourceNotification
        if (handle == IntPtr.Zero) return;
        while (WaitForSingleObject(handle, 0xFFFFFFFF) == 0)   // INFINITE
        {
            try { Low?.Invoke(); } catch { }
            // The handle stays signalled while memory is low; don't spin.
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateMemoryResourceNotification(int type);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
