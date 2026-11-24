using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Repository;

/// <summary>
/// The scans a host makes when it starts (repositories, endpoints, DTOs, mappers) list the types of the app's and the
/// modules' assemblies. An assembly with a type that cannot be loaded, because it was built against assemblies or types
/// this application no longer has or does not deploy, fails the scan with a message that names it and what is missing,
/// and says what to do, instead of a bare <see cref="ReflectionTypeLoadException"/>.
/// </summary>
public class AssemblyScanTests
{
    private const string Missing = "EntityFrameworkCore.Triggered.Abstractions, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null";

    /// <summary>A module's assembly built for an older framework: two of its types need what this application lacks.</summary>
    private sealed class OutdatedModule : Assembly
    {
        public override Type[] GetTypes() => throw new ReflectionTypeLoadException(
            [typeof(string), null, null],
            [
                new FileNotFoundException("Could not load file or assembly.", Missing),
                new TypeLoadException("Could not load type 'Some.Framework.RenamedBuilder`1' from assembly 'Some.Framework, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null'."),
                new FileNotFoundException("Could not load file or assembly.", Missing),
            ]);

        public override AssemblyName GetName() => new("Some.Module.Data, Version=1.16.33.0");

        public override string FullName => "Some.Module.Data, Version=1.16.33.0, Culture=neutral, PublicKeyToken=null";
    }

    private static void AssertSaysWhatIsMissingAndWhatToDo(InvalidOperationException failure)
    {
        Assert.Contains("The types of Some.Module.Data 1.16.33.0 cannot be loaded", failure.Message);
        Assert.Contains($"the assembly {Missing}", failure.Message);
        Assert.Contains("the type Some.Framework.RenamedBuilder`1 from Some.Framework, Version=2.0.0.0", failure.Message);

        // Both causes, since the loader cannot tell them apart: a module built for an older framework, or a dependency
        // that was not deployed.
        Assert.Contains("Some.Module.Data may be built for an older version of the Shift Framework", failure.Message);
        Assert.Contains("update it to a version built for this one", failure.Message);
        Assert.Contains("something it needs may not be deployed", failure.Message);

        // Each missing piece once, and the loader's own exception kept underneath.
        Assert.Single(failure.Message.Split(Missing).Skip(1));
        Assert.IsType<ReflectionTypeLoadException>(failure.InnerException);
    }

    [Fact]
    public void A_module_built_for_an_older_framework_fails_the_scan_with_a_message_that_names_it_and_what_is_missing()
    {
        AssertSaysWhatIsMissingAndWhatToDo(Assert.Throws<InvalidOperationException>(() => AssemblyScan.TypesOf(new OutdatedModule())));

        // An assembly whose types load is listed as it is.
        Assert.Contains(typeof(AssemblyScanTests), AssemblyScan.TypesOf(typeof(AssemblyScanTests).Assembly));
    }

    [Fact]
    public void The_framework_scans_at_start_fail_with_that_message()
    {
        // The repository registration, which every host that hands over its data assemblies calls.
        AssertSaysWhatIsMissingAndWhatToDo(Assert.Throws<InvalidOperationException>(() => new ServiceCollection().RegisterShiftRepositories(new OutdatedModule())));

        // The endpoint discovery and the mapper validation at start.
        AssertSaysWhatIsMissingAndWhatToDo(Assert.Throws<InvalidOperationException>(() => ShiftEntityEndpointDiscovery.Discover([new OutdatedModule()])));
        AssertSaysWhatIsMissingAndWhatToDo(Assert.Throws<InvalidOperationException>(() =>
            ShiftSoftware.ShiftEntity.EFCore.ShiftEntityMapperValidation.Validate(new ServiceCollection(), [new OutdatedModule()])));
    }
}
