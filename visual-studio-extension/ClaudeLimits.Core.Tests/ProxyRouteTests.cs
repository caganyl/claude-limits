using System.Collections.Generic;
using ClaudeLimits.Core;
using Xunit;

namespace ClaudeLimits.Core.Tests
{
    public class ProxyRouteTests
    {
        private static AnthropicClient Client(Dictionary<string, string> env, string setting = "auto")
        {
            var c = new AnthropicClient(null, name => env.TryGetValue(name, out var v) ? v : null);
            c.ProxySetting = setting;
            return c;
        }

        [Fact]
        public void Auto_WithEnvProxy_TriesProxyThenDirect()
        {
            using (var c = Client(new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://zhlevproxy2:8080" }))
                Assert.Equal(new[] { "proxy:http://zhlevproxy2:8080", "direct" }, c.RoutesFor("api.anthropic.com"));
        }

        [Fact]
        public void Auto_WithoutEnvProxy_UsesWindowsSystemProxy()
        {
            using (var c = Client(new Dictionary<string, string>()))
                Assert.Equal(new[] { "system" }, c.RoutesFor("api.anthropic.com"));
        }

        [Fact]
        public void ExplicitSettings()
        {
            var env = new Dictionary<string, string> { ["HTTP_PROXY"] = "http://zhlevproxy2:8080" };
            using (var direct = Client(env, "direct"))
                Assert.Equal(new[] { "direct" }, direct.RoutesFor("api.anthropic.com"));
            using (var fixedProxy = Client(env, "proxy.local:3128"))
                Assert.Equal(new[] { "proxy:http://proxy.local:3128" }, fixedProxy.RoutesFor("api.anthropic.com"));
        }

        [Fact]
        public void NoProxy_BypassesMatchingHosts()
        {
            var env = new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://p:8080", ["NO_PROXY"] = "localhost,.anthropic.com" };
            using (var c = Client(env))
                Assert.Equal(new[] { "direct" }, c.RoutesFor("api.anthropic.com"));
            Assert.True(AnthropicClient.BypassesProxy("git.corp.local", "*.corp.local:443"));
            Assert.False(AnthropicClient.BypassesProxy("api.anthropic.com", "corp.local"));
        }
    }
}
