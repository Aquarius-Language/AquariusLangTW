# Call-path optimization: validation and limits

Validated on Windows x64 with .NET 8.0.31 and the installed Chrome/Edge browsers.
This change implements specialized call conventions (roadmap item 7). Browser
execution uses suspension only for asynchronous native results as part of that
call path. It does not change Painter source or the portable bytecode format.

## Correctness checks

| Check | Result |
| --- | --- |
| Standard Release .NET suite | 1,248 passed; 27 optional graphics/window checks initially skipped |
| Opt-in desktop graphics, GPU, and package smoke suite | 757 passed; 2 separately gated application-window checks skipped |
| Separately gated native application-window checks | Both passed |
| JavaScript unit tests | 93 passed |
| Existing real-browser package/graphics/gameplay smoke suite | 44 passed |
| Existing resize, density, paused redraw, callback readback, input, disposal, marble, and WebGL smoke checks | Passed |
| Painter's unchanged integration suite on a separate fresh export | 24 groups passed in Chrome and 24 in Edge |
| Painter's unchanged bottle on the updated desktop runtime | Two real GPU frames completed and a PNG capture was produced |

The optional runs cover all 27 checks skipped by the standard .NET invocation.
New regression coverage checks argument lifetime, callback reentry, argument
evaluation order, alias replacement, serialized programs, asynchronous thenables,
public Promise compatibility, errors, and cancellation.

Painter was compiled from its original three source files into a separate
`.web-build/call-performance/painter.bottle`. The existing Painter web wrapper and
integration test script ran from a workspace copy. Its original project was not
edited. This is a validated development build, not a newly published compiler
release or a replacement of Painter's deployed outputs.

## Measurements

Nine-sample medians after warmup; timing thresholds are not part of the tests.
Desktop sample order alternates between the legacy and borrowed native paths.

| Isolated workload | Baseline | Optimized | Observation |
| --- | --- | --- | --- |
| Desktop native-call loop, allocated bytes | 25,122,424 | 20,642,424 | 17.8% fewer bytes |
| Desktop native-call loop, execution time | 21.77 ms | 21.01 ms | Small improvement in this run |
| Browser native-call loop, public Promise path versus direct entry | 3.29 ms | 2.20 ms | Approximately 1.49x faster direct calls |

The browser benchmark above runs both paths inside the new VM. A separate local
comparison against the pre-change VM measured 4.42 ms versus 2.18 ms for 20,000
native calls. Neither microbenchmark measures rendered frame latency.

The real-WebGPU benchmark substitutes only VM and host modules in the existing
Painter export, stops the animation loop before manual measurement, waits for GPU
completion, and explicitly disposes the device between variants. The last isolated
run measured these end-to-end latencies:

| Painter operation | Existing export | Candidate runtime |
| --- | --- | --- |
| Full redraw at 1440x900, document 960x600 | 158.3 ms | 102.7 ms |
| Brush 0, 40-pixel stroke, width 8 | 5.2 ms | 3.5 ms |
| Brush 3, 40-pixel stroke, width 8 | 88.7 ms | 84.3 ms |
| Brush 8, 40-pixel stroke, width 8 | 153.7 ms | 184.2 ms |

These GPU-inclusive timings varied materially across runs, and one brush remained
slower in the last run. They do not support claiming a consistent drastic Painter
speedup. The implementation reduces VM call overhead and desktop allocation, but
GPU buffer/bind-group creation, rendering, image readback, and resource reclamation
remain separate costs. A decisive end-to-end improvement needs additional work
on the measured renderer bottlenecks rather than a larger claim for this change.

The reusable benchmark commands and compatibility design are documented in
[call-conventions.md](call-conventions.md). Raw development reports and the
separate Painter export are under `.web-build/call-performance`.
