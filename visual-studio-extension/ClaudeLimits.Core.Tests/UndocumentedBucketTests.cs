using System.Linq;
using ClaudeLimits.Core;
using Xunit;

namespace ClaudeLimits.Core.Tests
{
    public class UndocumentedBucketTests
    {
        private const string Json = @"{
            ""five_hour"": { ""utilization"": 6 },
            ""seven_day"": { ""utilization"": 16 },
            ""iguana_necktie"": { ""utilization"": 97, ""resets_at"": ""2026-11-05T07:59:00Z"" }
        }";

        [Fact]
        public void CodenameBucket_IsFlagged_AndIgnoredForStatusAndColour()
        {
            var parsed = UsageParser.Parse(Json);
            var iguana = parsed.Windows.Single(w => w.Id == "iguana_necktie");
            Assert.True(iguana.IsUndocumented);
            Assert.False(parsed.Windows.Single(w => w.Id == "session").IsUndocumented);

            var snap = new Snapshot { Subscription = new SubscriptionUsage { Windows = parsed.Windows } };
            Assert.Equal("7g 16%", Formatting.CompactText(snap, DisplayMode.Highest));
            Assert.Equal(Level.Ok, Formatting.SnapshotLevel(snap));
        }
    }
}
