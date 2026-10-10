# Aquarius color mapping lab

A complete desktop UI written in AquariusLang with `匯入("Processing")`.
Choose a palette, adjust the mapping, and inspect how scalar values become colors.
The controls, field, legend, and cursor probe are drawn with Processing; no external
UI toolkit or image assets are required.

![Color mapping lab](preview.png)

## Run

From the repository root, with .NET 8 SDK and the native graphics bridge installed:

```powershell
# Only needed if the native graphics bridge has not been built yet.
./native/build.ps1
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/color_mapping/main.aqua
```

The example is also copied into VM build and release outputs. From an extracted
Windows release's `vm` directory, run:

```powershell
.\AquariusDesktop.exe .\examples\color_mapping\main.aqua
```

## Controls

| Control | Action |
| --- | --- |
| Palette cards / **1–4** | Ocean, Viridis, Ember, or Balance |
| Flow / Peaks / Rings / **D** | Choose or cycle the generated scalar field |
| Low / High sliders | Set the input range within 0–100; handles stay at least one unit apart |
| Gamma slider | Adjust the response from 0.25 to 2.50 |
| Reverse palette / **R** | Exchange the low and high colors |
| Stepped colors / **B** | Switch between a continuous palette and equal-width color bands |
| Color bands slider | Choose 3–16 bands when stepped colors are enabled |
| Move over the field | Inspect the sampled value, hex color, RGB channels, and palette position |
| Reset / **0** | Restore the Ocean / Flow / 0–100 / gamma 1 defaults |
| Save preview / **S** | Save the entire UI as `color-mapping-####.png` beside the script |
| **Esc** | Close the application |

Dragging a slider continues to work outside its track and clamps at the ends.
Resizing the window scales and centers the 1200×820 composition; pointer coordinates
are converted back to that layout. The probe remains at its last sample when the
cursor leaves the field. The application uses `noLoop()` and `redraw()` to draw on input.

## Mapping example

The `正規化` function implements this pipeline:

```text
t = constrain(map(value, low, high, 0, 1), 0, 1)
t = pow(t, gamma)
if reversed: t = 1 - t
if banded: t = min(bands - 1, floor(t * bands)) / (bands - 1)
color = lerpColor(adjacent palette stops, local position)
```

For a range of 20–80, value 50 maps to 0.5. Gamma 2 changes its position to 0.25;
reversing then gives 0.75. Values outside the input range clamp to its endpoint
colors. Band bins have equal widths in the transformed palette position, with
the exact upper endpoint included in the last bin.

`色帶` interpolates five RGB anchors for each palette. Viridis is an approximation
using five anchors. Balance is a diverging palette with a light neutral center.
The field caches 196×90 scalar samples independently of the colors, then uses a
256-entry palette lookup table to update a `PImage`. Lookup quantization happens
after normalization, so even a narrow input range retains intermediate colors.
The probe reports the same cached sample and lookup color used by the image.
The legend shows the full 0–100 input domain, including any clamped portions.

Replace `取樣` with your own data function to reuse the UI for a different dataset.
The source is organized into mapping functions, input handlers, image caching,
and drawing helpers followed by `設定` and `繪製` callbacks.

## Verification

Headless tests execute the example's actual functions to check clamping, gamma,
reversal, band endpoints, RGB/hex conversion, generated field bounds, narrow ranges,
slider limits, palette selection, and reset behavior:

```powershell
dotnet test AquariusTests -c Release -m:1 --filter FullyQualifiedName~ColorMappingExample
```

Enable the GPU test to also render the UI, change its controls, verify framebuffer
colors with wgpu, and check PNG export:

```powershell
$env:AQUARIUS_WGPU_TESTS = '1'
dotnet test AquariusTests -c Release -m:1 --filter FullyQualifiedName~ColorMappingExample
Remove-Item Env:AQUARIUS_WGPU_TESTS
```

For a finite preview capture:

```powershell
$env:AQUARIUS_GRAPHICS_FRAMES = '1'
$env:AQUARIUS_GRAPHICS_CAPTURE = "$PWD/color-mapping.png"
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/color_mapping/main.aqua
Remove-Item Env:AQUARIUS_GRAPHICS_FRAMES, Env:AQUARIUS_GRAPHICS_CAPTURE
```

See the [Processing guide](../../graphics/Processing.md) for API details and platform limitations.
