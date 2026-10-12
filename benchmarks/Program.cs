using BenchmarkDotNet.Running;

namespace Snail.Toolkit.HttpBuilder.Extensions.Benchmarks;

/// <summary>Runs the benchmarks named on the command line, or offers all of them.</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
