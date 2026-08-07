using PalmierShell.Core;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

public class AgentShutdownTests {
    /// The initial settings load is fire-and-forget; teardown destroying the
    /// agent handle while the load is in flight used to let the continuation
    /// configure a freed native handle (0xC0000005). The gate holds the load
    /// open until after destroy, and the guard must bounce the continuation.
    [Fact]
    public async Task ShutdownDuringTheInitialSettingsLoadNeverTouchesTheCore() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        try {
            var gate = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var vm = new AgentViewModel(agent, new TimelineViewModel(project), new MediaPanelViewModel(),
                new UndoStack(() => "", _ => true), () => gate.Task);
            vm.Shutdown();
            CoreApi.palmier_agent_destroy(agent);
            agent = IntPtr.Zero;
            gate.SetResult(AppSettings.Default with {
                Keys = new Dictionary<string, string> { ["anthropic"] = "sk-test" },
            });
            await vm.Ready;
            Assert.False(vm.HasApiKey);
        } finally {
            if (agent != IntPtr.Zero) CoreApi.palmier_agent_destroy(agent);
            CoreApi.palmier_project_destroy(project);
        }
    }
}
