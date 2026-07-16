using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Infrastructure;

public sealed partial class WindowsGpuActivityProbe(
    GpuActivityOptions options,
    ILogger<WindowsGpuActivityProbe> logger) : IGpuActivityProbe
{
    public async Task<GpuActivitySnapshot> SampleAsync(
        IReadOnlyCollection<int> processIds,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var requested = processIds.Where(static processId => processId > 0).ToHashSet();
        if (requested.Count == 0)
        {
            return new GpuActivitySnapshot(true, new Dictionary<int, double>());
        }

        if (!OperatingSystem.IsWindows())
        {
            return new GpuActivitySnapshot(false, new Dictionary<int, double>());
        }

        IntPtr query = IntPtr.Zero;
        try
        {
            var status = NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out query);
            if (status != NativeMethods.ErrorSuccess)
            {
                return Unavailable(status, "open the GPU performance query");
            }

            status = NativeMethods.PdhAddEnglishCounter(
                query,
                @"\GPU Engine(*)\Utilization Percentage",
                IntPtr.Zero,
                out var counter);
            if (status != NativeMethods.ErrorSuccess)
            {
                return Unavailable(status, "add the GPU utilization counter");
            }

            status = NativeMethods.PdhCollectQueryData(query);
            if (status != NativeMethods.ErrorSuccess)
            {
                return Unavailable(status, "collect the first GPU sample");
            }

            await Task.Delay(options.SampleInterval, cancellationToken);
            status = NativeMethods.PdhCollectQueryData(query);
            if (status != NativeMethods.ErrorSuccess)
            {
                return Unavailable(status, "collect the second GPU sample");
            }

            var samples = ReadSamples(counter, out status);
            if (status != NativeMethods.ErrorSuccess)
            {
                return Unavailable(status, "read GPU utilization values");
            }

            return new GpuActivitySnapshot(true, AggregateSamples(samples, requested));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            logger.LogDebug(ex, "Windows GPU performance counters are unavailable.");
            return new GpuActivitySnapshot(false, new Dictionary<int, double>());
        }
        finally
        {
            if (query != IntPtr.Zero)
            {
                _ = NativeMethods.PdhCloseQuery(query);
            }
        }
    }

    internal static IReadOnlyDictionary<int, double> AggregateSamples(
        IEnumerable<GpuCounterSample> samples,
        IReadOnlySet<int> requestedProcessIds)
    {
        var result = new Dictionary<int, double>();
        foreach (var sample in samples)
        {
            if (sample.Status > NativeMethods.PdhCstatusNewData ||
                !TryGetProcessId(sample.InstanceName, out var processId) ||
                !requestedProcessIds.Contains(processId) ||
                !double.IsFinite(sample.UtilizationPercent))
            {
                continue;
            }

            result.TryGetValue(processId, out var current);
            result[processId] = Math.Clamp(current + Math.Max(0, sample.UtilizationPercent), 0, 100);
        }

        return result;
    }

    internal static bool TryGetProcessId(string instanceName, out int processId)
    {
        var match = ProcessIdPattern().Match(instanceName);
        processId = 0;
        return match.Success && int.TryParse(match.Groups["processId"].Value, out processId);
    }

    private GpuActivitySnapshot Unavailable(uint status, string operation)
    {
        logger.LogDebug("Could not {GpuOperation}; PDH status 0x{PdhStatus:X8}.", operation, status);
        return new GpuActivitySnapshot(false, new Dictionary<int, double>());
    }

    private static IReadOnlyList<GpuCounterSample> ReadSamples(IntPtr counter, out uint status)
    {
        uint bufferSize = 0;
        uint itemCount = 0;
        status = NativeMethods.PdhGetFormattedCounterArray(
            counter,
            NativeMethods.PdhFmtDouble,
            ref bufferSize,
            ref itemCount,
            IntPtr.Zero);
        if (status == NativeMethods.ErrorSuccess && itemCount == 0)
        {
            return [];
        }

        if (status != NativeMethods.PdhMoreData || bufferSize == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArray(
                counter,
                NativeMethods.PdhFmtDouble,
                ref bufferSize,
                ref itemCount,
                buffer);
            if (status != NativeMethods.ErrorSuccess)
            {
                return [];
            }

            var itemSize = Marshal.SizeOf<PdhFormattedCounterValueItem>();
            var samples = new List<GpuCounterSample>(checked((int)itemCount));
            for (var index = 0; index < itemCount; index++)
            {
                var item = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(
                    IntPtr.Add(buffer, checked((int)index * itemSize)));
                var name = Marshal.PtrToStringUni(item.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    samples.Add(new GpuCounterSample(
                        name,
                        item.Value.DoubleValue,
                        item.Value.Status));
                }
            }

            return samples;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [GeneratedRegex("(?:^|_)pid_(?<processId>[0-9]+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProcessIdPattern();

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)]
        public uint Status;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValueItem
    {
        public IntPtr Name;
        public PdhFormattedCounterValue Value;
    }

    private static class NativeMethods
    {
        public const uint ErrorSuccess = 0;
        public const uint PdhCstatusNewData = 1;
        public const uint PdhMoreData = 0x800007D2;
        public const uint PdhFmtDouble = 0x00000200;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhOpenQuery(
            string? dataSource,
            IntPtr userData,
            out IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        public static extern uint PdhAddEnglishCounter(
            IntPtr query,
            string fullCounterPath,
            IntPtr userData,
            out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
        public static extern uint PdhGetFormattedCounterArray(
            IntPtr counter,
            uint format,
            ref uint bufferSize,
            ref uint itemCount,
            IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        public static extern uint PdhCloseQuery(IntPtr query);
    }
}

internal sealed record GpuCounterSample(
    string InstanceName,
    double UtilizationPercent,
    uint Status = 0);
