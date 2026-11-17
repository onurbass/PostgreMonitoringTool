using PgMonitor.Core.Model;

namespace PgMonitor.Tests;

public class SkeletonTests
{
    [Fact]
    public void Default_collector_kind_is_simulated()
    {
        Assert.Equal(CollectorKind.Simulated, default(CollectorKind));
    }
}
