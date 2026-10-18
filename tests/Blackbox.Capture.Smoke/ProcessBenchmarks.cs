using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Blackbox.Capture.Smoke;

internal readonly record struct ProcessPoint(double ClockSeconds, double CpuSeconds, long WorkingSet, long PrivateBytes, int Handles)
{
    public object Delta(ProcessPoint previous) => new
    {
        CpuPercentOfOneCore = 100 * (CpuSeconds - previous.CpuSeconds) / (ClockSeconds - previous.ClockSeconds),
        WorkingSetMiB = WorkingSet / 1048576d, PrivateMiB = PrivateBytes / 1048576d, Handles
    };
}

internal static class ProcessBenchmarks
{
    public static ProcessPoint Read(Process process)
    {
        process.Refresh();
        return new(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, process.TotalProcessorTime.TotalSeconds,
            process.WorkingSet64, process.PrivateMemorySize64, process.HandleCount);
    }

    public static IReadOnlyList<int> Children(int parent)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new InvalidOperationException("Could not enumerate child processes.");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            var result = new List<int>();
            if (Process32First(snapshot, ref entry))
                do { if (entry.Parent == parent) result.Add((int)entry.Id); } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
