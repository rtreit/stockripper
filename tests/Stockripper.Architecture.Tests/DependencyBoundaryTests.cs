using Stockripper.Application.Brokers;
using Stockripper.Contracts;
using Stockripper.Domain.Execution;
using Stockripper.Infrastructure;
using Stockripper.Testing;

namespace Stockripper.Architecture.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void Domain_has_no_dependency_on_outer_projects()
    {
        var references = ReferenceNames(typeof(ExecutionMode).Assembly);

        Assert.DoesNotContain("Stockripper.Application", references);
        Assert.DoesNotContain("Stockripper.Infrastructure", references);
        Assert.DoesNotContain("Stockripper.Contracts", references);
        Assert.DoesNotContain("System.Net.Http", references);
    }

    [Fact]
    public void Application_has_no_dependency_on_infrastructure_or_transport()
    {
        var references = ReferenceNames(typeof(BrokerPreflightService).Assembly);

        Assert.DoesNotContain("Stockripper.Infrastructure", references);
        Assert.DoesNotContain("Stockripper.Contracts", references);
        Assert.DoesNotContain("Grpc.Core.Api", references);
        Assert.DoesNotContain("System.Net.Http", references);
    }

    [Fact]
    public void Broker_fake_has_no_network_dependency()
    {
        var references = ReferenceNames(typeof(FakeBrokerConnectionProbe).Assembly);

        Assert.DoesNotContain("System.Net.Http", references);
        Assert.DoesNotContain("System.Net.Sockets", references);
    }

    [Fact]
    public void Assembly_markers_resolve_expected_projects()
    {
        Assert.Equal("Stockripper.Contracts", typeof(ContractVersions).Assembly.GetName().Name);
        Assert.Equal("Stockripper.Infrastructure", typeof(InfrastructureAssembly).Assembly.GetName().Name);
    }

    private static HashSet<string> ReferenceNames(System.Reflection.Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(reference => reference.Name!)
        .ToHashSet(StringComparer.Ordinal);
}
