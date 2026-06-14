using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.DataLevelAccess;
using ShiftSoftware.ShiftEntity.Tests.DataLevelAccess.Scenario;
using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataLevelAccess;

/// <summary>
/// Slice 2.5 — the scoped DI wiring: <c>AddShiftEntityDataLevelAccess()</c> registers
/// <see cref="IAccessibleItemsSource"/> → <see cref="TypeAuthAccessibleItemsSource"/> and
/// <see cref="DataLevelAccessContext"/>, both <b>scoped</b>. These tests pin the lifetime contract from the 2.1
/// notes — the source's memoization lives exactly one request (shared within a scope, never across), nothing
/// resolves from the root (captive-dependency guard), and the <c>TryAdd</c> registration keeps the source a
/// pluggable seam. Resolution is exercised through a container shaped like a real host: scoped
/// <c>ITypeAuthService</c> (the scenario's <see cref="TypeAuthContext"/> is one), scoped
/// <see cref="ICurrentUserProvider"/>, singleton <see cref="IHashIdService"/>.
/// </summary>
public class DataLevelAccessRegistrationTests
{
    /// <summary>Container shaped like a real host (the three prerequisites at their production lifetimes), with
    /// scope validation on — the same startup check that catches a captive scoped service in Development.</summary>
    private static ServiceProvider BuildProvider(Action<IServiceCollection>? registerFirst = null)
    {
        var services = new ServiceCollection();

        // What TypeAuth's AddTypeAuth contributes: a per-request ITypeAuthService built from the caller's claims.
        services.AddScoped<ITypeAuthService>(_ => ScopedTypeAuth.ToCompany(Companies.Intermediary));
        // What AddShiftEntityWebSharedCore / AddShiftEntity contribute.
        services.AddScoped<ICurrentUserProvider>(_ => FakeCurrentUserProvider.Anonymous());
        services.AddSingleton<IHashIdService>(new RecordingHashIdService());

        registerFirst?.Invoke(services);

        services.AddShiftEntityDataLevelAccess();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void Context_ResolvesFromAScope_AndDrivesThePolicy()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        // All three dependencies wire up…
        var context = scope.ServiceProvider.GetRequiredService<DataLevelAccessContext>();

        // …into a context the policy can actually run on: the canonical cross-column OR filters through the
        // DI-resolved context exactly as through a hand-built one.
        var access = new DataLevelAccessBuilder<Vehicle>();
        access.On(VehicleDataLevel.Companies).Keys(x => x.CompanyID, x => x.IntermediaryCompanyID);
        var visible = new DataLevelAccessPolicy<Vehicle>(access)
            .ApplyQueryFilter(VehicleScenario.SampleVehicles().AsQueryable(), Access.Read, context)
            .Select(v => v.Id).OrderBy(id => id).ToList();

        Assert.Equal(new long[] { 3, 4, 5, 6 }, visible);
    }

    [Fact]
    public void Source_IsSharedWithinAScope_AndPrivateAcrossScopes()
    {
        using var provider = BuildProvider();

        // One instance per scope: every query/row check in a request shares one memoizing source…
        using var scope = provider.CreateScope();
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<IAccessibleItemsSource>(),
            scope.ServiceProvider.GetRequiredService<IAccessibleItemsSource>());

        // …and no instance outlives its scope: the next request (next caller!) gets a fresh source, so one user's
        // memoized accessible ids can never serve another.
        using var otherScope = provider.CreateScope();
        Assert.NotSame(
            scope.ServiceProvider.GetRequiredService<IAccessibleItemsSource>(),
            otherScope.ServiceProvider.GetRequiredService<IAccessibleItemsSource>());
    }

    [Fact]
    public void ScopedServices_DoNotResolveFromTheRoot()
    {
        // The "never singleton / never captive" rule from the 2.1 lifetime notes, enforced by scope validation:
        // resolving the per-request services from the root provider (what a captive dependency amounts to) throws.
        using var provider = BuildProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<DataLevelAccessContext>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IAccessibleItemsSource>());
    }

    [Fact]
    public void CustomSource_RegisteredBeforehand_Wins()
    {
        // TryAdd keeps IAccessibleItemsSource a pluggable seam (D11): a host that registered its own source first
        // is not overridden by the framework registration.
        using var provider = BuildProvider(services =>
            services.AddScoped<IAccessibleItemsSource, FakeAccessibleItemsSource>());
        using var scope = provider.CreateScope();

        Assert.IsType<FakeAccessibleItemsSource>(scope.ServiceProvider.GetRequiredService<IAccessibleItemsSource>());
    }

    /// <summary>Stand-in custom source — only ever resolved (to prove the TryAdd seam), never invoked.</summary>
    private sealed class FakeAccessibleItemsSource : IAccessibleItemsSource
    {
        public AccessibleItemsByAccess GetByAccess(DynamicAction action, params string[]? selfIds)
            => throw new NotImplementedException();
    }
}
