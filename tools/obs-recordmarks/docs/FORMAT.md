# RecordMarks JSON format v1

UTF-8。時刻・秒数設定から計算する値は整数ミリ秒。動画名と保存パスはOBSの録画時点の値。すべての時間は出力フレーム数から求めた推定であり、PTS・音声解析による位置指定ではない。

## Per-video sidecar

`format = "obs-record-marks"`, `schema_version = 1`。

| Field | Meaning |
|---|---|
| `session_id` | Recording session identifier |
| `producer_version` | Extension version |
| `title` | User's recording topic, captured at session start |
| `started_at_utc`, `updated_at_utc` | ISO 8601 UTC timestamps |
| `state` | `recording`, `closed`, or `interrupted` |
| `timing.basis` | `recorded_output_frames` |
| `timing.fps_num`, `timing.fps_den`, `timing.frame_rate_divisor` | FPS rational and output encoder divisor |
| `timing.quality` | `output_frame_estimate`, `split_boundary_estimate`, or `unknown_prior_split` |
| `timing.encoder_latency_not_compensated` | Always true in v1 |
| `recording.path`, `recording.filename`, `recording.index` | Recorded file identity; 1-based index |
| `recording.start_session_ms`, `recording.end_session_ms` | Session-relative bounds; end omitted until finalized |
| `recording.duration_ms` | Known estimated file duration; otherwise omitted |
| `recording.file_offset_known` | Whether session time can be translated into this file's estimated time |
| `markers` | Array (including empty arrays) |

Each marker contains:

| Field | Meaning |
|---|---|
| `id` | Increasing ID within a session; IDs are never reused after undo |
| `kind` | `highlight`, `explanation`, `completion`, `custom` |
| `label` | Japanese display label |
| `note` | User-supplied note copied when marking |
| `file_index` | Recorded file index |
| `session_frame` | Output frame index, independent of split files |
| `session_time_ms` | Session-relative estimate, excludes pauses |
| `time_ms` | File-relative estimate; omitted if offset unknown |
| `before_ms`, `after_ms` | Requested review context |
| `review_start_ms`, `review_end_ms` | File-relative context bounds; capped at known file end |
| `context_crosses_file` | Requested context extends into a neighboring file |
| `file_time_unknown` | Present and true if recorded file offset is unknown |
| `created_at_utc` | Marker creation wall time |

Consumers MUST NOT interpret a missing `time_ms` as zero. For split files, the start offset is sampled at the `file_changed` signal and MUST be treated as an estimate. Uncompensated encoder buffering can also affect unsplit files.

## Recording-session manifest

`format = "obs-record-marks-session"`, `schema_version = 1`。

Contains `session_id`, `title`, `producer_version`, UTC timestamps, FPS fields, `state` (`recording`, `stopped`, `interrupted`), `partial_capture`, `files`, and `markers`.

`files[]` contains `index`, `path`, `filename`, `sidecar`, `start_session_ms`, optional `end_session_ms`, `file_offset_known`, and `timing_quality`. `markers[]` contains the base marker fields and session-relative times. Derive file-relative time only if the matching file's `file_offset_known` is true. Deduplicate manifest/sidecar imports by `(session_id, id)`; prefer the newest complete session snapshot when files disagree after undo or recovery.

The timing equation is:

`time_ms = round(frame * 1000 * fps_den * frame_rate_divisor / fps_num)`

The marker frame is the last emitted output frame (`max(total_frames - 1, 0)`). A split boundary samples the same counter, because the normal interleaved muxer increments it before delivering the split keyframe. This is not a guarantee of the exact timestamp in every muxer/encoder mode.

## Identity and recovery

The normal filename includes the video's extension (`example.mkv.markers.json`). Existing sidecars/manifests are not overwritten for a new session; a suffix is added. `.bak` is the previous successful JSON snapshot. Session and per-file snapshots are each saved atomically but the entire multi-file batch is not one transaction. After a crash, use the newest valid complete manifest or sidecar. Scripts added during an already split recording do not guess a prior file boundary.
