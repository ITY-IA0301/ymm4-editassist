# EditAssist録画マークJSON v1

UTF-8のJSON。`schema: "EditAssist.RecordingMarkers"`、`schema_version: 1`。時刻の単位は秒です。位置はすべて近似です。

## 録画全体

- `session_id`：録画ごとの一意の文字列。
- `producer / producer_version`：ツールと版。
- `started_at_utc / finished_at_utc`：UTC日時。終了前はfinished_at_utcなし。
- `state`：recording / paused / stopped / script_unloaded / obs_shutdown。
- `timing_method`：monotonic_recording_events。
- `timing_is_approximate`：true。
- `recording_duration_seconds`：一時停止を除いた録画時間。終了時に追加。
- `segments`：分割動画一覧。分割なしでも1要素。
- `markers`：追加順のマーク。取り消したものは含まない。

## segmentsの各要素

- `index`：1始まり。
- `video_file`：OBSが通知したパス。
- `recording_start_seconds`：録画全体での開始位置。
- `recording_end_seconds`：次の分割または録画終了位置。録画中の最終要素は未定。
- `boundary_is_approximate`：true。

## markersの各要素

- `id`：録画内の単調増加番号。取り消した番号は再利用しない。
- `category`：highlight / explanation / completed。
- `label`：見せ場 / 説明 / 完成。
- `segment_index / video_file`：対象の分割動画。
- `position_seconds`：その動画内の位置。
- `recording_position_seconds`：一時停止を除いた録画全体の位置。
- `created_at_utc`：UTC日時。
- `timing_is_approximate`：true。
- `context`：before_seconds / after_seconds / recording_start_seconds / recording_end_seconds。前後の確認範囲。録画終端を超えることがある。

読み込み側は識別子と形式版を確認し、有限かつ非負の時刻・分割一覧を検証します。動画の移動後は再指定して対応付けます。同名だけで別ファイルへ自動的に結び付けず、確認範囲は実動画の長さで制限します。
