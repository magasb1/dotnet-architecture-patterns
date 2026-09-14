# Detection delivery

The worker reads a continuous MPEG-TS feed from the stream owner. Discovery and lease
renewal are separate from frame delivery. Listing, lease and result requests have two-second
deadlines; the video subscription remains open until cancellation or disconnection.

Each stream retains one newest waiting decoded frame. A new frame replaces and frees the
previous waiting frame, while frames already taken for inference remain valid until the batch
finishes. This bounds retained waiting frames by the number of streams and avoids processing
an old FIFO backlog when inference falls behind.

Tracking runs in inference order. Each stream then publishes through its own background loop,
with one active POST and one newest waiting result. Slow delivery replaces older waiting
results instead of delaying inference. Timed-out results are not retried: the next result is
more useful for a live overlay. Disabling a stream cancels its publisher and frees its waiting frame.

The desktop uses `WatchLiveDetections`, an authenticated server-streaming gRPC call. The API
samples the latest result every 50 ms for a local owner and every 200 ms for a remote owner,
sending only changed timestamps. This bounds remote requests and avoids buffering stale
results for slow viewers. The client reconnects after 500 ms on transient RPC failures;
older servers fall back to non-overlapping polling at the configured rate, bounded to 1–20 Hz.

## Measurements

The `StorageDemo.Live` meter exposes these histograms in milliseconds:

| Instrument | Interval |
| --- | --- |
| `live.detection.queue.duration` | Decoded-frame arrival to selection for inference |
| `live.detection.duration` | Existing preprocessing, inference and decoding of model output |
| `live.detection.frame.duration` | Decoded-frame arrival to completed tracking |
| `live.detection.post.duration` | Result HTTP request, including failures and timeouts |

Frame timestamps now anchor at decoded-frame arrival rather than after the first inference.
They still do not represent the encoder's capture clock. These metrics do not measure network
transit before decoding, result waiting time, or desktop presentation delay. Aligning boxes to
the exact video presentation timestamp remains separate work; faster delivery alone cannot
eliminate motion-related overlay lag.
