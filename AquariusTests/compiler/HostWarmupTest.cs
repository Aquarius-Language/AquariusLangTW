using AquariusLang.Wasm;

namespace AquariusTests.Compiler;

public class HostWarmupTest {
    // Every method of the host service path must compile ahead of the first guest call; a failure would surface here.
    [Fact] public async Task TheHostServicePathCompilesInTheBackground() {
        await WasmRuntime.WarmUp().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(WasmRuntime.WarmUp().IsCompletedSuccessfully);
        Assert.Same(WasmRuntime.WarmUp(), WasmRuntime.WarmUp());
    }
}
