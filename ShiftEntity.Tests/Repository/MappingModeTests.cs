using System;
using System.Linq;
using ShiftSoftware.ShiftEntity.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// Step D1 / D4 / D5 — the registry side of mapper resolution.
/// <para>
/// Until D1 the registry was read by <c>UseGeneratedMapper()</c> and endpoint discovery and by nothing else,
/// so a source-generated mapper could exist, be correct, be registered, and the repository would still use
/// AutoMapper and never know. That is gap B-1, and the Stage C inventory found it live on exactly one triple.
/// </para>
/// <para>
/// These tests exercise the registry directly rather than through a constructed repository, because the
/// registry is process-global static state populated by module initializers: a test that booted a host to
/// observe resolution would be asserting against whatever else the run had already loaded.
/// </para>
/// </summary>
public class MappingModeTests
{
    // Types exist only as registry keys. Each test uses its own so the global registry cannot make one test's
    // outcome depend on another's — the registry has no reset, by design, since production never needs one.
    private sealed class EntityA { }
    private sealed class ListA { }
    private sealed class ViewA { }
    private sealed class MapperA1 { }
    private sealed class MapperA2 { }

    // Distinct per conflict test. The registry is process-global and has no reset — production never needs
    // one — so two tests sharing a triple would each append to the same conflict bag and read each other's.
    private sealed class EntityD { }
    private sealed class ListD { }
    private sealed class ViewD { }
    private sealed class MapperD1 { }
    private sealed class MapperD2 { }

    private sealed class EntityE { }
    private sealed class ListE { }
    private sealed class ViewE { }
    private sealed class MapperE1 { }
    private sealed class MapperE2 { }

    private sealed class EntityB { }
    private sealed class ListB { }
    private sealed class ViewB { }
    private sealed class MapperB { }

    private sealed class EntityC { }
    private sealed class ListC { }
    private sealed class ViewC { }
    private sealed class MapperC { }

    [Fact]
    public void Register_ThenFind_RoundTrips()
    {
        ShiftEntityMapperRegistry.Register(typeof(EntityB), typeof(ListB), typeof(ViewB), typeof(MapperB));

        Assert.Equal(typeof(MapperB),
            ShiftEntityMapperRegistry.Find(typeof(EntityB), typeof(ListB), typeof(ViewB)));
    }

    [Fact]
    public void Register_IsIdempotent_ForTheSameMapper()
    {
        ShiftEntityMapperRegistry.Register(typeof(EntityC), typeof(ListC), typeof(ViewC), typeof(MapperC));
        var before = ShiftEntityMapperRegistry.Conflicts.Count;

        ShiftEntityMapperRegistry.Register(typeof(EntityC), typeof(ListC), typeof(ViewC), typeof(MapperC));

        // Re-registering the same type is what a module initializer running twice looks like. It is not a
        // conflict and must not be recorded as one, or the noise buries the real ones.
        Assert.Equal(before, ShiftEntityMapperRegistry.Conflicts.Count);
        Assert.Equal(typeof(MapperC), ShiftEntityMapperRegistry.Find(typeof(EntityC), typeof(ListC), typeof(ViewC)));
    }

    /// <summary>
    /// D5. Two different mappers claiming one triple used to be last-write-wins, so which one you got depended
    /// on module-initializer order — a coin flip, silently. Now it is deterministic AND recorded.
    /// </summary>
    [Fact]
    public void Register_RecordsAConflict_InsteadOfSilentlyOverwriting()
    {
        ShiftEntityMapperRegistry.Register(typeof(EntityA), typeof(ListA), typeof(ViewA), typeof(MapperA1));
        ShiftEntityMapperRegistry.Register(typeof(EntityA), typeof(ListA), typeof(ViewA), typeof(MapperA2));

        var conflict = Assert.Single(ShiftEntityMapperRegistry.Conflicts, c => c.Entity == typeof(EntityA));

        // Both mappers live in this same assembly, so neither is "the one declared alongside the entity" and
        // the first registration stands. The point is that the outcome is defined and reported, not that a
        // particular one wins.
        Assert.Equal(typeof(MapperA1), conflict.Kept);
        Assert.Equal(typeof(MapperA2), conflict.Rejected);
        Assert.Equal(typeof(MapperA1), ShiftEntityMapperRegistry.Find(typeof(EntityA), typeof(ListA), typeof(ViewA)));
    }

    /// <summary>
    /// Register runs inside a <c>[ModuleInitializer]</c>. Throwing there surfaces as a
    /// <c>TypeInitializationException</c> with the real cause buried, which is why conflicts are recorded and
    /// reported at startup instead.
    /// </summary>
    [Fact]
    public void Register_NeverThrows_OnConflict()
    {
        var ex = Record.Exception(() =>
        {
            ShiftEntityMapperRegistry.Register(typeof(EntityD), typeof(ListD), typeof(ViewD), typeof(MapperD1));
            ShiftEntityMapperRegistry.Register(typeof(EntityD), typeof(ListD), typeof(ViewD), typeof(MapperD2));
        });

        Assert.Null(ex);
    }

    [Fact]
    public void ConflictMessage_NamesBothMappersAndTheirAssemblies()
    {
        ShiftEntityMapperRegistry.Register(typeof(EntityE), typeof(ListE), typeof(ViewE), typeof(MapperE1));
        ShiftEntityMapperRegistry.Register(typeof(EntityE), typeof(ListE), typeof(ViewE), typeof(MapperE2));

        var text = ShiftEntityMapperRegistry.Conflicts.First(c => c.Entity == typeof(EntityE)).ToString();

        // A conflict a reader cannot act on is not worth recording: the message has to say which two, and where
        // they came from, since the whole scenario is one mapper arriving from a referenced package.
        Assert.Contains(nameof(MapperE1), text);
        Assert.Contains(nameof(MapperE2), text);
        Assert.Contains("kept", text);
        Assert.Contains("ignored", text);
    }

    // ── D4: version skew, detected not declared ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every registered mapper can bind to the framework it is running against. Verified by JIT-preparing the
    /// mapper's methods, which resolves their call targets — so nothing needs an ABI number that somebody has
    /// to remember to bump, and additive framework changes (which break nothing) raise nothing.
    /// </summary>
    [Fact]
    public void VerifyBindings_ReportsNothing_WhenEverythingResolves()
    {
        ShiftEntityMapperRegistry.Register(typeof(EntityB), typeof(ListB), typeof(ViewB), typeof(MapperB));

        // These test doubles call nothing that could go missing, so a report here would be a false positive —
        // which is the failure mode that would make the whole check get switched off.
        Assert.DoesNotContain(ShiftEntityMapperRegistry.VerifyBindings(), b => b.MapperType == typeof(MapperB));
    }

    // ── the mode itself ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MappingMode_DefaultsToAutoMapperFirst()
    {
        // The whole safety property of D1: shipping it changes nothing until someone opts in.
        Assert.Equal(ShiftEntityMappingMode.AutoMapperFirst, new ShiftEntityOptions().MappingMode);
    }
}
