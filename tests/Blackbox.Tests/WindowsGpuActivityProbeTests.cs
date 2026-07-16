using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class WindowsGpuActivityProbeTests
{
    [Fact]
    public void AggregateSamples_sums_valid_engines_for_requested_processes()
    {
        GpuCounterSample[] samples =
        [
            new("pid_42_luid_0x00000000_eng_0_engtype_3D", 24.5),
            new("pid_42_luid_0x00000000_eng_1_engtype_Copy", 3.5),
            new("pid_42_luid_0x00000000_eng_2_engtype_VideoDecode", 99, 0xC0000BC6),
            new("pid_84_luid_0x00000000_eng_0_engtype_3D", 80)
        ];

        var result = WindowsGpuActivityProbe.AggregateSamples(samples, new HashSet<int> { 42 });

        Assert.Equal(28, result[42]);
        Assert.DoesNotContain(84, result.Keys);
    }

    [Theory]
    [InlineData("pid_42_luid_0x0_eng_0_engtype_3D", 42)]
    [InlineData("gpu_pid_9876_luid_0x0", 9876)]
    public void TryGetProcessId_parses_gpu_engine_instance_names(string instanceName, int expected)
    {
        Assert.True(WindowsGpuActivityProbe.TryGetProcessId(instanceName, out var processId));
        Assert.Equal(expected, processId);
    }
}
