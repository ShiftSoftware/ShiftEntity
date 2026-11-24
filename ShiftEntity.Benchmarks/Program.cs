using BenchmarkDotNet.Running;

namespace ShiftSoftware.ShiftEntity.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
