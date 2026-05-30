using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests;

/// <summary>
/// Slice 0.1 — proves the first-ever ShiftEntity test project is wired up and the
/// xUnit v3 runner executes. Real coverage (scenario model, scoped TypeAuthContext,
/// characterization of current data-level behavior) arrives in slices 0.2 / 0.3.
/// </summary>
public class BuildSanityTests
{
    [Fact]
    public void TestHarness_RunsGreen()
    {
        Assert.Equal(4, 2 + 2);
    }
}
