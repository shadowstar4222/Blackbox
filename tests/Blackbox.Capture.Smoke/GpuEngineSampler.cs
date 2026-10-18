using System.Runtime.InteropServices;

namespace Blackbox.Capture.Smoke;

internal sealed class GpuEngineSampler : IDisposable
{
    private IntPtr _query;
    private IntPtr _counter;
    public GpuEngineSampler()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0 ||
            PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
        { Dispose(); return; }
        PdhCollectQueryData(_query);
    }

    public IReadOnlyDictionary<string, double>? Read(IReadOnlyCollection<int> ids)
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArray(_counter, 0x200, ref size, ref count, IntPtr.Zero) != 0x800007D2 || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, 0x200, ref size, ref count, buffer) != 0) return null;
            var result = new Dictionary<string, double>();
            var stride = Marshal.SizeOf<Item>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<Item>(IntPtr.Add(buffer, index * stride));
                if (item.Value.Status > 1 || !double.IsFinite(item.Value.Number)) continue;
                var name = Marshal.PtrToStringUni(item.Name) ?? "";
                var process = ids.FirstOrDefault(id => name.Contains($"pid_{id}_", StringComparison.Ordinal));
                if (process == 0) continue;
                var offset = name.IndexOf("engtype_", StringComparison.Ordinal);
                if (offset < 0) continue;
                var key = $"{process}:{name[(offset + 8)..]}";
                result.TryGetValue(key, out var previous);
                result[key] = Math.Clamp(previous + item.Value.Number, 0, 100);
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); _query = IntPtr.Zero; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Value { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
    [StructLayout(LayoutKind.Sequential)] private struct Item { public IntPtr Name; public Value Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
