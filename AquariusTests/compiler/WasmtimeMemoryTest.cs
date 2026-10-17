using System.Buffers.Binary;
using AquariusLang.Desktop.runtime;
using AquariusLang.Wasm;

namespace AquariusTests.Wasm;

public class WasmtimeMemoryTest
{
    private const string Source = """
        (module
          (import "aquarius_v2" "observe" (func $observe (result i32)))
          (memory (export "memory") 1 16)
          (func (export "grow") (result i32)
            i32.const 1 memory.grow drop
            i32.const 65536 i32.const 42 i32.store
            i32.const 42)
          (func (export "callback") (result i32)
            i32.const 1 memory.grow drop
            call $observe)
          (func (export "trap") (result i32)
            i32.const 1 memory.grow drop unreachable))
        """;

    [Fact]
    public void MemoryViewsRefreshAfterGrowthCallbacksReentryAndTraps()
    {
        IWasmInstance? instance = null;
        using (instance = new WasmtimeEngine().Instantiate(Wasmtime.Module.ConvertText(Source), new Dictionary<string, Delegate> {
            ["observe"] = (Func<int>)(() => {
                // The callback sees the grown memory before invoking the guest again.
                instance!.Memory(2 * 65536, 4).Clear();
                instance.Invoke("grow");
                instance.Memory(3 * 65536, 4).Clear();
                return 7;
            })
        })) {
            instance.Memory(0, 4).Fill(1);
            Assert.Equal(42, instance.Invoke("grow"));
            Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(instance.Memory(65536, 4)));
            Assert.Equal(7, instance.Invoke("callback"));
            Assert.Throws<Wasmtime.TrapException>(() => instance.Invoke("trap"));
            instance.Memory(4 * 65536, 4).Clear();
            Assert.Throws<ArgumentOutOfRangeException>(() => instance.Memory(-1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => instance.Memory(0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => instance.Memory(int.MaxValue, int.MaxValue));
        }
        Assert.Throws<ObjectDisposedException>(() => instance.Memory(0, 0));
        Assert.Throws<ObjectDisposedException>(() => instance.Invoke("grow"));
    }
}
