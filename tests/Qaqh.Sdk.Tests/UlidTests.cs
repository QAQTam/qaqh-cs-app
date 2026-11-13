using System.Text.RegularExpressions;
using Qaqh.Sdk.Serialization;
using Xunit;

namespace Qaqh.Sdk.Tests;

public class UlidTests
{
    [Fact]
    public void GeneratesCanonical26CharCrockford()
    {
        var ulid = Ulid.NewUlid();
        Assert.Matches(new Regex("^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{26}$"), ulid);
    }

    [Fact]
    public void UniqueAcrossBurst()
    {
        var set = new HashSet<string>(Enumerable.Range(0, 10_000).Select(_ => Ulid.NewUlid()));
        Assert.Equal(10_000, set.Count);
    }

    [Fact]
    public void SortableByTimestampPrefix()
    {
        var a = Ulid.NewUlid();
        Thread.Sleep(5);
        var b = Ulid.NewUlid();
        Assert.True(string.CompareOrdinal(a, b) < 0, $"{a} !< {b}");
    }
}
