using System.Runtime.InteropServices;
using AquariusREPL.Graphics;
using AquariusREPL.Application;
using AquariusLang.Application;

namespace AquariusLangVMTesting.Application;

public sealed class NativeApplicationFact : FactAttribute {
    public NativeApplicationFact() { if (!OperatingSystem.IsWindows() || System.Environment.GetEnvironmentVariable("AQUARIUS_APPLICATION_NATIVE_TESTS") != "1") Skip = "Set AQUARIUS_APPLICATION_NATIVE_TESTS=1 on Windows with the built native bridge."; }
}
public class DesktopIntegrationTest {
    [NativeApplicationFact] public void AttachedWindowsKeepIndependentCallbacksCursorsAndAccessibility() {
        Assert.NotEqual(0,Native.aqua_init());IntPtr first=IntPtr.Zero,second=IntPtr.Zero;
        try {
            Native.aqua_hint(0x20004,0);Native.aqua_hint(0x22001,0);
            first=Native.aqua_window(80,60,"first");second=Native.aqua_window(80,60,"second");
            Assert.NotEqual(IntPtr.Zero,first);Assert.NotEqual(IntPtr.Zero,second);
            var files=new DesktopFiles();var a=new DesktopWindow(()=>first,files);var b=new DesktopWindow(()=>second,files);a.Poll();b.Poll();
            a.SetCursor(CursorShape.Hand);b.SetCursor(CursorShape.Text);
            var accessibleA=new DesktopAccessibility(first);var accessibleB=new DesktopAccessibility(second);
            accessibleA.Update(new[]{new AccessibilityNode("a","status","first")});accessibleB.Update(new[]{new AccessibilityNode("b","status","second")});
            Native.aqua_wgpu_handles(first,out _,out var hwndA);Native.aqua_wgpu_handles(second,out _,out var hwndB);
            var guid=typeof(IAccessibleTree).GUID;Assert.Equal(0,AccessibleObjectFromWindow(hwndA,unchecked((uint)-4),ref guid,out var treeA));
            Assert.Equal("first",treeA.get_accName(1));Assert.Equal(0,AccessibleObjectFromWindow(hwndB,unchecked((uint)-4),ref guid,out var treeB));Assert.Equal("second",treeB.get_accName(1));
            PostMessageW(hwndA,0x10,IntPtr.Zero,IntPtr.Zero);Native.aqua_poll();Assert.Contains(a.Poll(),e=>e.Type=="closeRequested");Assert.DoesNotContain(b.Poll(),e=>e.Type=="closeRequested");a.ResolveClose(CloseDecision.Cancel);
            Native.aqua_destroy(first);first=IntPtr.Zero;Assert.Contains(a.Poll(),e=>e.Type=="destroyed");b.SetCursor(CursorShape.Arrow);
            PostMessageW(hwndB,0x10,IntPtr.Zero,IntPtr.Zero);Native.aqua_poll();Assert.Contains(b.Poll(),e=>e.Type=="closeRequested");b.ResolveClose(CloseDecision.Accept);
            GC.KeepAlive(a);GC.KeepAlive(b);GC.KeepAlive(accessibleA);GC.KeepAlive(accessibleB);
        } finally {if(first!=IntPtr.Zero)Native.aqua_destroy(first);if(second!=IntPtr.Zero)Native.aqua_destroy(second);Native.aqua_terminate();}
    }
    [NativeApplicationFact] public void ExistingWindowDeliversLifecycleCaptureCloseTextAndAccessibility() {
        Assert.NotEqual(0,Native.aqua_init()); IntPtr window=IntPtr.Zero;
        try {
            Native.aqua_hint(0x20004,0);Native.aqua_hint(0x22001,0);window=Native.aqua_window(80,60,"application integration");Assert.NotEqual(IntPtr.Zero,window);
            var adapter=new DesktopWindow(()=>window,new DesktopFiles());Assert.Contains(adapter.Poll(),e=>e.Type=="created");adapter.SetTitle("Updated title");adapter.SetCursor(CursorShape.Hand);
            adapter.SetCustomCursor(new PixelImage(1,1,new byte[]{1,2,3,0}),0,0);Native.aqua_wgpu_handles(window,out _,out var hwnd);
            adapter.CapturePointer(0);adapter.ReleasePointer(0);Native.aqua_poll();Assert.Contains(adapter.Poll(),e=>e.Type=="captureLost");
            PostMessageW(hwnd,0x10,IntPtr.Zero,IntPtr.Zero);Native.aqua_poll();Assert.Contains(adapter.Poll(),e=>e.Type=="closeRequested");Assert.Equal(0,Native.aqua_should_close(window));adapter.ResolveClose(CloseDecision.Cancel);
            Native.aqua_text_input(window,1);PostMessageW(hwnd,0x102,(IntPtr)'繁',IntPtr.Zero);Native.aqua_poll();string? text=null;while(true){var p=Native.aqua_text_event(window,out int kind,out _);if(kind==0)break;if(kind==1)text=Marshal.PtrToStringUTF8(p);}Assert.Equal("繁",text);
            var accessibility=new DesktopAccessibility(window);accessibility.Update(new[]{new AccessibilityNode("status","status","Saved","complete")});
            var guid=typeof(IAccessibleTree).GUID;int result=AccessibleObjectFromWindow(hwnd,unchecked((uint)-4),ref guid,out var tree);Assert.Equal(0,result);Assert.Equal(1,tree.get_accChildCount());Assert.Equal("Saved",tree.get_accName(1));Assert.Equal("complete",tree.get_accValue(1));
            PostMessageW(hwnd,0x10,IntPtr.Zero,IntPtr.Zero);Native.aqua_poll();adapter.ResolveClose(CloseDecision.Accept);Assert.Equal(1,Native.aqua_should_close(window));
            GC.KeepAlive(adapter);GC.KeepAlive(accessibility);
        } finally { if(window!=IntPtr.Zero)Native.aqua_destroy(window);Native.aqua_terminate(); }
    }
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd,uint message,IntPtr wparam,IntPtr lparam);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr hwnd,uint objectId,ref Guid id,[MarshalAs(UnmanagedType.Interface)]out IAccessibleTree accessible);
}
