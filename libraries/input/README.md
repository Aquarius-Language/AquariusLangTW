# input

`AndroidInput` / `Android輸入`: touches, the back gesture, window insets, the soft keyboard and vibration on Android.
Every other target registers the module with `IsSupported()` false; its other functions validate their arguments and
then fail with `AndroidInput.<name>: Android input is unavailable on this platform.`

| Function | Arguments | Result |
| --- | --- | --- |
| `IsSupported` / `是否支援` | | boolean |
| `Poll` / `讀取輸入` | | snapshot: `orientation` (`portrait`/`landscape`), `keyboardVisible`, `backRequested`, `insets` (`left`, `top`, `right`, `bottom`), `touches` |
| `ShowKeyboard` / `顯示鍵盤` | boolean | shows or hides the soft keyboard |
| `Vibrate` / `震動` | integer milliseconds 1..10000 | |
| `ResolveBack` / `處理返回` | `"Accept"`, `"Defer"` or `"Cancel"` | `Accept` closes the application |

Each touch has `id`, `phase` (`down`, `move`, `up`, `cancel`), `x`, `y` (density-independent units), `pressure`,
`tool` (`touch` or `pen`), `timestamp` (monotonic seconds) and `history`. `Poll` returns the touches since the previous
`Poll` and whether the back gesture happened since then.

## How it works

`src/input.c` listens (`aqua_window_listen`) to the activity's window, which `aqua_android_window()` returns once a
graphics library (Processing, WGPU, ...) created it: pointer events become touches (the button event that follows a
pointer event marks it `down` or `up`), `AQUA_EVENT_BACK` sets `backRequested`, and pausing (`AQUA_EVENT_LIFECYCLE`)
reports the touches in progress as `cancel`. Insets and keyboard visibility come from `aqua_android_insets`; the soft
keyboard goes through the window's text input (or `AquariusActivity.showKeyboard` without a window); vibration and
`Accept` (`Activity.finish()`) use JNI. `android.permission.VIBRATE` is declared in `aqua.toml`.

## Dependencies

None.

## Differences from the old implementation

- Touches arrive only while a library has created the activity's window (the old Java host observed the whole
  activity). Scripts poll inside a sketch or window loop, as before.
- `history` is always an empty array: the platform layer reports each motion sample as its own event instead of
  batching historical samples. A cancelled gesture of the platform appears as `up`; pausing reports `cancel`.
- Wrong argument counts report the runtime's message instead of `AndroidInput.<name>: Invalid argument count.`
- Snapshots, insets and touches are native objects (`型別` shows `AndroidInput.Snapshot`, ...) instead of `module`.

## Tests

`tests/*.aqua` run on desktop and web (unsupported module, validation order and messages). Android behavior needs a
device run.
