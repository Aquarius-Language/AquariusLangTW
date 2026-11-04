# remotewindow

`匯入("RemoteWindow")` / `匯入("遠端視窗")`: the remote window protocol for scripts, on every target (Windows, Linux,
Android, the web). A desktop runtime started with `AQUARIUS_REMOTE_WINDOW=host:port` shows every window it opens on a
viewer at that address instead of the screen; this library is that viewer, the relay a server puts between the two,
and a sender for programs that want to show images on a viewer themselves.

The application side needs nothing: Processing, WGPU, OpenGL, ImGui, Clay and SaturnUI programs open their windows as
always (`aqua/window.h`), and the runtime streams them. Frames travel as the 64-pixel tiles that changed since the last
frame, losslessly compressed (QOI); at most two frames wait for the viewer, so a slow viewer sees the latest picture
rather than a backlog, and an application nobody watches stops sending. Input goes back as the window events the
application would get from a screen. The wire format, with its encoders and decoders, is defined once in
`runtime/sdk/include/aqua/remote_window.h` and shared by the runtime's sender (`runtime/platform/desktop/window_remote.c`)
and this library, so the two ends cannot drift apart.

## The viewer

```aquarius
var RW = import("RemoteWindow");
var Network = import("Network");
var listener = Network.ListenTCP("127.0.0.1", 18791);
# ... start the application with AQUARIUS_REMOTE_WINDOW=127.0.0.1:18791 (Terminal's Environment option) ...
var stream = listener.Accept(5000);
var viewer = RW.Viewer(token);                     # the token the application was given, or no argument
var records = viewer.Feed(stream.Read(1 << 20));   # [{"type": "hello" | "open" | "frame" | ..., "window", ...}]
var list = viewer.Paint(id, x, y, width, height);  # a UI draw list: hand it to p.drawUI, return it from Immediate.paint
viewer.Send(id, event);                            # input, raw or as Interaction's events
stream.Write(viewer.Outgoing());                   # acknowledgements, input and sizes back to the application
```

| Viewer member | Result |
| --- | --- |
| `Feed(bytes)` / `輸入` | the records of the whole messages received so far: `hello` (`version`, `token`), `open` (`window`, `width`, `height`, `flags`, `title`), `close`, `title`, `size`, `frame` (`number`, `width`, `height`, `scale`), `cursor` (`cursor`: an Interaction cursor name or `hidden`), `text` (`enabled`, `x`, `y`, `width`, `height`: text input and its caret box), `clipboard` (`text`); a malformed stream, another protocol version or a wrong token fails, and the viewer stays failed |
| `Outgoing()` / `待送` | the bytes to send back (empty when there is nothing) |
| `Windows()` / `視窗清單`, `Window(id)` / `視窗` | window records: `window`, `title`, `width`, `height` (logical units), `scale`, `flags`, `cursor`, `textInput`, `frameWidth`, `frameHeight`, `frame`, `closed` |
| `Paint(id, x, y, width, height)` / `繪製` | the window's frame drawn in that rectangle as a UI draw list (`libraries/saturn/README.md`). The renderer keeps the frame as a texture: the first list carries all of it, later lists only the rows of tiles that changed. Painting acknowledges the frame |
| `Image(id)` / `影像` | the frame as a shared image object (`Window.ToProcessingImage`, `Images`, ONNX), or null before the first frame; acknowledges it |
| `Send(id, event)` / `傳送事件` | sends one event; false for events that are not input (`click`, `longPress`, `command`: the application derives those itself) |
| `View(id, width, height[, scale])` / `檢視` | the size to show the window at (logical units; the application's window takes it) and the viewer's pixel density (the application renders that many pixels per unit). Also how a viewer attaches: the application sends its next frame whole |
| `Paste(text)` / `貼上` | the viewer's clipboard text, for the application's next paste |
| `Close(id)` / `關閉` | asks the window to close, as its close button would |
| `ImeRect(id)` / `輸入法位置` | `[x, y, width, height]` of the text caret relative to the painted rectangle while the window takes text, else null (SaturnUI's `Immediate.ime`) |
| `Forget(id)` / `移除` | drops a closed window; returns a draw list that frees its texture in the renderer |

`Send` takes either shape of event hash, so any UI library can host a remote window:

- raw, as `aqua/window.h` has it and `Interaction.Feed` takes it: `type` `pointer`, `button`, `scroll` (`x`, `y` wheel
  steps), `key`, `text`, `composition` (`phase`), `focus`, `enter`, `close`, ... with numeric `action` and `mods`;
- Interaction's, as SaturnUI's `Immediate` widgets receive them (`event.Fields()`): `press`, `release`, `move`, `drag`,
  `dragStart`, `dragEnd`, `scroll` and `zoom` (turned back into wheel steps with the event's own `conventions`),
  `key` (a string `action`), `text`, `composition`, `focus`.

Positions are relative to the rectangle the window was last painted in and scale to the window's own units, so a window
painted at twice its size gets the right coordinates.

## The relay

A server between an application and viewers that come and go (WellPlay's notebook server) forwards whole messages and
learns what it needs without decoding frames:

| Relay member | Result |
| --- | --- |
| `FromApplication(bytes)` / `來自應用程式` | `{"forward": bytes, "messages": [...]}`: the whole messages to pass on, and the records of everything but frames (`hello` with its `token` tells which program connected) |
| `FromViewer(bytes)` / `來自檢視器` | the whole viewer messages to pass on; anything that is not a viewer message fails |
| `Snapshot()` / `快照` | what a viewer joining now needs first: HELLO and every open window with its title, cursor and text input. Its `View` then has the application send each frame whole |
| `Windows()` / `視窗清單` | the open windows |

## The sender

`Sender([token])` / `傳送端` lets any program show images on a viewer: `Open(width, height, title[, flags])` → id,
`Frame(id, image[, scale])` or `Frame(id, rgbaBytes, width, height[, scale])` → the frame number (0 when nothing
changed: nothing is sent), `Behind(id)` (frames the viewer has not shown yet), `Title`, `Cursor(id, name)`,
`TextInput(id, enabled[, x, y, width, height])`, `Resize(id, width, height)`, `Clipboard(text)`, `Close(id)`,
`Outgoing()` and `Feed(bytes)`, which gives `{"type": "event", "window", "event": raw event hash}` (ready for
`Interaction.Feed`), `{"type": "view", ...}` and `{"type": "paste", "text"}`.

## Tests

`tests/protocol.aqua` (every target): a sender's windows, frames, title, cursor, text input and clipboard reach a
viewer; images and draw lists with only the changed regions; raw and Interaction events, sizes and pastes back; the
relay with a viewer joining late; failures. `tests/desktop_window.aqua` (desktop, needs a GPU): an unchanged Processing
sketch runs with `AQUARIUS_REMOTE_WINDOW` pointing at this same program, which shows its frames, clicks, types,
resizes it to twice the density and closes it.
