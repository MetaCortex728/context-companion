using ContextCompanion.Core.Packets;

namespace ContextCompanion.Core.Tests;

public class UlidTests
{
    [Fact]
    public void EncodesTimestampAndRandomnessInCrockfordBase32()
    {
        Assert.Equal(new string('0', 26), Ulid.Encode(0, new byte[10]));
        Assert.Equal("7ZZZZZZZZZZZZZZZZZZZZZZZZZ", Ulid.Encode(Ulid.MaxTimestamp, Enumerable.Repeat((byte)0xff, 10).ToArray()));
        // Timestamp of the example in the ULID specification.
        Assert.Equal(1469918176385L, Ulid.GetTimestamp("01ARYZ6S41TSV4RRFFQ69G5FAV"));
        Assert.StartsWith("01ARYZ6S41", Ulid.Encode(1469918176385L, new byte[10]), StringComparison.Ordinal);
        Assert.Equal("0000000000000000000000000Z", Ulid.Encode(0, [0, 0, 0, 0, 0, 0, 0, 0, 0, 31]));
        Assert.Equal("00000000000000000000000010", Ulid.Encode(0, [0, 0, 0, 0, 0, 0, 0, 0, 0, 32]));
    }

    [Theory]
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FAV", true)]
    [InlineData("01aryz6s41tsv4rrffq69g5fav", false)] // only the canonical upper-case form
    [InlineData("81ARYZ6S41TSV4RRFFQ69G5FAV", false)] // timestamp overflow
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FA", false)]
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FAVX", false)]
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FAI", false)] // I, L, O and U are not in the alphabet
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FAU", false)]
    [InlineData("01ARYZ6S41TSV4RRFFQ69G5FA/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognisesCanonicalUlids(string? value, bool valid) => Assert.Equal(valid, Ulid.IsValid(value));

    [Fact]
    public void IdentifiersWithinOneMillisecondAreStrictlyIncreasing()
    {
        var time = new ManualTime(Samples.Start);
        var generator = new UlidGenerator(time);
        var ids = Enumerable.Range(0, 10_000).Select(_ => generator.NewId()).ToArray();

        Assert.All(ids, id => Assert.True(Ulid.IsValid(id)));
        Assert.All(ids, id => Assert.Equal(Samples.Start.ToUnixTimeMilliseconds(), Ulid.GetTimestamp(id)));
        for (int i = 1; i < ids.Length; i++)
            Assert.True(string.CompareOrdinal(ids[i - 1], ids[i]) < 0, $"{ids[i - 1]} !< {ids[i]}");
    }

    [Fact]
    public void SameMillisecondIncrementsTheRandomPartByOne()
    {
        var generator = new UlidGenerator(new ManualTime(Samples.Start), random => random.Clear());
        string first = generator.NewId(), second = generator.NewId();
        Assert.EndsWith("0000000000000000", first, StringComparison.Ordinal);
        Assert.EndsWith("0000000000000001", second, StringComparison.Ordinal);
    }

    [Fact]
    public void NewMillisecondDrawsFreshRandomnessAndSortsLater()
    {
        var time = new ManualTime(Samples.Start);
        var generator = new UlidGenerator(time, random => random.Fill(0xff));
        string first = generator.NewId();
        time.Advance(TimeSpan.FromMilliseconds(1));
        string second = generator.NewId();
        Assert.True(string.CompareOrdinal(first, second) < 0);
        Assert.Equal(Samples.Start.ToUnixTimeMilliseconds() + 1, Ulid.GetTimestamp(second));
    }

    [Fact]
    public void ClockSteppingBackwardsKeepsOrder()
    {
        var time = new ManualTime(Samples.Start);
        var generator = new UlidGenerator(time);
        string first = generator.NewId();
        time.Advance(TimeSpan.FromSeconds(-5));
        string second = generator.NewId();
        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Fact]
    public void RandomOverflowWithinOneMillisecondFailsInsteadOfRepeating()
    {
        var generator = new UlidGenerator(new ManualTime(Samples.Start), random => random.Fill(0xff));
        generator.NewId();
        Assert.Throws<InvalidOperationException>(generator.NewId);
    }
}
