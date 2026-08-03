using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class AgentRetryTests {
    [Fact]
    public void Retry_OnAFreshAgent_HasNothingToResume() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        try {
            Assert.Equal(0, CoreApi.palmier_agent_retry(agent));
        } finally {
            CoreApi.palmier_agent_destroy(agent);
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// A failed turn leaves the user's message at the end of the conversation,
    /// which is what makes a retry resumable instead of a duplicate send.
    [Fact]
    public void Retry_AfterAFailedTurn_IsOffered() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        try {
            // No key and a provider that requires one: the turn fails fast,
            // without touching the network.
            CoreApi.palmier_agent_configure(agent, "zai", "", "glm-4.6");
            if (Environment.GetEnvironmentVariable("ZAI_API_KEY") is { Length: > 0 }) return;

            Assert.Equal(1, CoreApi.palmier_agent_send(agent, "trim the first clip"));
            string events = WaitForDone(agent);
            Assert.Contains("\"retryable\":true", events);
            Assert.Equal(1, CoreApi.palmier_agent_retry(agent));
        } finally {
            CoreApi.palmier_agent_destroy(agent);
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void Retry_WhileATurnIsRunning_IsRefused() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        try {
            CoreApi.palmier_agent_configure(agent, "zai", "", "glm-4.6");
            if (Environment.GetEnvironmentVariable("ZAI_API_KEY") is { Length: > 0 }) return;

            CoreApi.palmier_agent_send(agent, "hello");
            // Busy or already finished; either way a second turn must not start
            // on top of a running one.
            if (CoreApi.palmier_agent_busy(agent) == 1) Assert.Equal(0, CoreApi.palmier_agent_retry(agent));
            WaitForDone(agent);
        } finally {
            CoreApi.palmier_agent_destroy(agent);
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Collects poll output until the turn reports done (or we give up).
    static string WaitForDone(IntPtr agent) {
        var all = new System.Text.StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline) {
            if (CoreApi.PollAgent(agent) is { } json) {
                all.Append(json);
                if (json.Contains("\"done\"")) break;
            }
            Thread.Sleep(30);
        }
        return all.ToString();
    }
}
