using System.Diagnostics;
using System.Reflection;
using Dmr;
using IqChannelizer;

namespace BadSdr.Core.Dsp;

/// <summary>Inspect loaded assemblies, so the diagnostics also catch stale/copied Debug DLLs.</summary>
public static class DspBuildInfo
{
    public static bool AssemblyIsOptimized(Assembly assembly) =>
        assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    public static bool IsOptimized => AssemblyIsOptimized(typeof(DmrReceiver).Assembly) &&
        AssemblyIsOptimized(typeof(ChannelizerFactory).Assembly) && AssemblyIsOptimized(typeof(DmrProcessor).Assembly);

    public static string Summary => IsOptimized ? "DSP optimized" : "DSP UNOPTIMIZED — use Release for RF performance";
    public static object Details => new
    {
        tieredCompilation = AppContext.GetData("System.Runtime.TieredCompilation"),
        tieredCompilationEnvironmentOverride = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ??
            Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"),
        core = Describe(typeof(DmrProcessor).Assembly),
        dmr = Describe(typeof(DmrReceiver).Assembly),
        channelizer = Describe(typeof(ChannelizerFactory).Assembly)
    };

    private static object Describe(Assembly assembly) => new
    {
        assembly = assembly.GetName().Name,
        configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
        optimized = AssemblyIsOptimized(assembly)
    };
}
