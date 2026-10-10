using System;
using System.IO;
using System.Linq;
using AquariusLang.Object;
using static AquariusLang.Application.ApplicationValues;

namespace AquariusLang.Application;

public sealed partial class ApplicationRuntime {
    private void RegisterFiles() {
        var m = Module("Files");
        Bind(m, "Capabilities", 0, 0, _ => Record(host.Files.Capabilities));
        Bind(m, "Resolve", 1, 1, a => Resource(host.Files.Resolve(Text(a[0]))));
        Bind(m, "Stat", 1, 1, a => Record(Await(host.Files.Stat(Owned<FileResource>(a[0]), cancellation))));
        Bind(m, "ReadBytes", 1, 1, a => Binary(Await(files.Read(Owned<FileResource>(a[0]), cancellation))));
        Bind(m, "ReadText", 2, 2, a => new StringObj(Await(files.ReadText(Owned<FileResource>(a[0]), Text(a[1]), cancellation))));
        Bind(m, "SaveBytes", 2, 3, a => new BooleanObj(Await(files.Save(Owned<FileResource>(a[0]), Bytes(a[1]), a.Length == 3 && Boolean(a[2]), cancellation))));
        Bind(m, "SaveText", 3, 4, a => new BooleanObj(Await(files.SaveText(Owned<FileResource>(a[0]), Text(a[1]), Text(a[2]), a.Length == 4 && Boolean(a[3]), cancellation))));
        Bind(m, "Enumerate", 1, 1, a => new ArrayObj(Await(host.Files.Enumerate(Owned<FileResource>(a[0]), cancellation)).Select(r => (IObject)Resource(r)).ToArray()));
        Action(m, "CreateDirectory", 1, 1, a => Await(host.Files.CreateDirectory(Owned<FileResource>(a[0]), cancellation)));
        Action(m, "Delete", 1, 2, a => Await(host.Files.Delete(Owned<FileResource>(a[0]), a.Length == 2 && Boolean(a[1]), cancellation)));
        Action(m, "Copy", 2, 3, a => Await(host.Files.Copy(Owned<FileResource>(a[0]), Owned<FileResource>(a[1]), a.Length == 3 && Boolean(a[2]), cancellation)));
        Action(m, "Move", 2, 3, a => Await(host.Files.Move(Owned<FileResource>(a[0]), Owned<FileResource>(a[1]), a.Length == 3 && Boolean(a[2]), cancellation)));
        Bind(m, "Temporary", 1, 1, a => { var lease = Await(host.Files.Temporary(Boolean(a[0]), cancellation)); asyncDisposables.Add(lease); var result = Resource(lease.Resource); Action(result, "Dispose", 0, 0, _ => { Await(lease.DisposeAsync()); objects.Remove(result); }); return result; });
        Bind(m, "Open", 2, 2, a => StreamObject(Await(host.Files.Open(Owned<FileResource>(a[0]), Enum.Parse<ResourceOpenMode>(Text(a[1]), true), cancellation))));
        var paths = Module("Paths");
        Bind(paths, "Normalize", 1, 1, a => new StringObj(PortablePath.Normalize(Text(a[0]))));
        Bind(paths, "Join", 1, 1, a => new StringObj(PortablePath.Join((a[0] as ArrayObj ?? throw new ArgumentException("Expected path array.")).Elements.Select(Text).ToArray())));
        Bind(paths, "Name", 1, 1, a => new StringObj(PortablePath.Name(Text(a[0])))); Bind(paths, "Extension", 1, 1, a => new StringObj(PortablePath.Extension(Text(a[0]))));
        Bind(paths, "Parent", 1, 1, a => new StringObj(PortablePath.Parent(Text(a[0])))); Bind(paths, "Relative", 2, 2, a => new StringObj(PortablePath.Relative(Text(a[0]), Text(a[1]))));
    }
    private ModuleObj StreamObject(Stream stream) {
        var m = Record(new { stream.CanRead, stream.CanWrite, stream.CanSeek }); objects.Add(m, stream); disposables.Add(stream);
        Bind(m, "Read", 1, 1, a => { var live = Owned<Stream>(m); int count = Integer(a[0]); limits.Bytes(count); var bytes = new byte[count]; int used = Await(live.ReadAsync(bytes.AsMemory(), cancellation)); return Binary(bytes[..used]); });
        Action(m, "Write", 1, 1, a => Await(Owned<Stream>(m).WriteAsync(Bytes(a[0]), cancellation)));
        Bind(m, "Seek", 2, 2, a => new DoubleObj(Owned<Stream>(m).Seek(checked((long)WholeNumber(a[0])), Enum.Parse<SeekOrigin>(Text(a[1]), true))));
        Action(m, "Flush", 0, 0, _ => Owned<Stream>(m).FlushAsync(cancellation).GetAwaiter().GetResult());
        Action(m, "Close", 0, 0, _ => { if (!objects.Remove(m)) return; stream.Dispose(); }); return m;
    }
}
