# Boundaries and ownership

This is a small WinForms application, not a mediator/repository framework. The
important feature boundaries are explicit rather than enforced through a large
set of projects or generic abstractions.

## Connect and configure a camera

- `CameraSession` is the asynchronous boundary used by the viewer. It creates,
  accesses, and disposes its `ICameraBackend` on one dedicated STA thread with a
  message pump. Requests are serialized, and continuations never run inline on
  the COM thread.
- `DirectShowCameraBackend` selects exactly one device. Its capture graph and all
  extension-unit operations use that same device path. Multiple matching cameras
  are rejected rather than silently using another camera's calibration.
- `CameraCapture`, `CameraExtension`, and `NativeValidation` contain native/COM
  details. Callbacks only transfer owned frame data; they do not manipulate UI.
- Normal operations have an eight-second caller deadline. A timeout faults the
  session and suppresses further commands/frames. It **does not abort a native
  call**. Cleanup is queued on the owning thread; the viewer waits up to three
  seconds and keeps reconnect disabled until that thread actually exits. Closing
  the application is not held open by the background camera thread.
- `CalibrationRetryPolicy` owns the five-attempt, one-second backoff rule and has
  an injectable monotonic clock. No sleeps are needed on the UI thread. Unsupported
  formats and semantically invalid calibration are not retried.

## Measure a frame

`ThermalDecoder` validates native payload layout; `Thermometry` validates calibration
and performs numerical conversion independently of display processing. It returns
per-pixel `MeasurementQuality` as well as aggregate statistics. `TemperatureText`
and `TemperatureReport` retain range-limit meaning in UI and copied text.

The mandatory synthetic fixture under `IRCamera.Tests/Fixtures` contains
coefficients, telemetry, and expected numerical results. Missing fixtures fail
the tests. The regression suite runs without camera hardware.

## Inspect the image

`ImageProcessing` remains grayscale-only. `ThermalRenderer` owns its returned
bitmap until the canvas accepts it; failures dispose the unreturned bitmap.
`ThermalCanvas` owns displayed bitmaps, coordinate transforms, and marker history.
`MainForm` keeps presentation state and orchestrates asynchronous session results;
results from retired sessions cannot replace current state.

## Diagnostics and tests

`SessionDiagnostics` retains up to 128 events plus counters and render timings in
memory. Capture callback failures are recorded in Release builds. It does not
write frames or logs automatically. **Copy diagnostics** is an explicit user action;
the text can include the camera's device path/serial and should be reviewed before
sharing. Smoke modes write diagnostics under `artifacts` for development only.

Hardware-free tests use a scripted backend to exercise thread ownership, late
callbacks, transient errors, deadlines, shutdown, and retry timing. UI tests remain
useful for pixel/layout regressions but are not substitutes for lifecycle tests.

Render timing and superseded-frame counters provide evidence before introducing
buffer pools or caches. No pooling framework or new UI framework is required.
