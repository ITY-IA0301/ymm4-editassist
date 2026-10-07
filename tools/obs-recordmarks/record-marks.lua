-- OBS RecordMarks 0.1.0. Standalone OBS Lua script; no network or video writes.
local obs = obslua
local VERSION = "0.1.0"
local session, output, handler, pending = nil, nil, nil, {}
local hotkeys, callbacks = {}, {}
local config = {before = 30, after = 15, note = "", title = ""}
local status = "録画待ち。設定 → ホットキーでキーを割り当ててください。"
local last_press = -math.huge
local labels = {highlight = "見せ場", explanation = "説明", completion = "完成", custom = "メモ"}

-- Arrays are explicitly tagged so empty marker lists remain JSON arrays.
local function array(t) return setmetatable(t or {}, {__json_array = true}) end
local function quote(s)
    return '"' .. tostring(s):gsub('[%z\1-\31\\"]', function(c)
        local escapes = {['"'] = '\\"', ['\\'] = '\\\\', ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t'}
        return escapes[c] or string.format('\\u%04x', c:byte())
    end) .. '"'
end
local function json(value)
    local kind = type(value)
    if kind == "nil" then return "null" end
    if kind == "string" then return quote(value) end
    if kind == "boolean" then return value and "true" or "false" end
    if kind == "number" then
        assert(value == value and value ~= math.huge and value ~= -math.huge, "non-finite JSON number")
        -- Integer milliseconds avoid locale-dependent decimal serialization.
        return string.format("%.0f", value)
    end
    assert(kind == "table", "unsupported JSON value")
    local parts = {}
    local mt = getmetatable(value)
    if mt and mt.__json_array then
        for _, item in ipairs(value) do parts[#parts + 1] = json(item) end
        return "[" .. table.concat(parts, ",") .. "]"
    end
    local keys = {}
    for key in pairs(value) do keys[#keys + 1] = key end
    table.sort(keys)
    for _, key in ipairs(keys) do parts[#parts + 1] = quote(key) .. ":" .. json(value[key]) end
    return "{" .. table.concat(parts, ",") .. "}"
end
local function basename(path) return path:match("([^/\\]+)$") or path end
local function stamp() return os.date("!%Y-%m-%dT%H:%M:%SZ") end
local function notify(message, error_level)
    status = message
    obs.script_log(error_level and obs.LOG_ERROR or obs.LOG_INFO, "[RecordMarks] " .. message)
end
local function safe_write(path, document)
    local data = obs.obs_data_create_from_json(json(document))
    if not data then return false end
    -- OBS's UTF-8 file API supports Japanese Windows paths, unlike Lua io.open.
    local ok, result = pcall(obs.obs_data_save_json_safe, data, path, "tmp", "bak")
    obs.obs_data_release(data)
    return ok and result == true
end
local function exists(path)
    if obs.os_file_exists then return obs.os_file_exists(path) end
    local data = obs.obs_data_create_from_json_file(path)
    if data then obs.obs_data_release(data); return true end
    return false
end
local function reserve(path)
    if not exists(path) and not exists(path .. ".bak") then return path end
    local base = path:gsub("%.json$", "") .. "." .. session.id
    local candidate, n = base .. ".json", 2
    while exists(candidate) or exists(candidate .. ".bak") do
        candidate = base .. "." .. n .. ".json"; n = n + 1
    end
    return candidate
end
local function frames()
    return output and math.max(0, obs.obs_output_get_total_frames(output)) or 0
end
local function ms(frame)
    return math.floor(frame * 1000 * session.fps_den * session.divisor / session.fps_num + 0.5)
end
local function file_document(file)
    local list = array()
    local start = ms(file.start_frame)
    local finish = file.end_frame and ms(file.end_frame) or nil
    for _, mark in ipairs(session.markers) do
        if mark.file_index == file.index then
            local item = {}
            for key, value in pairs(mark) do item[key] = value end
            if file.offset_known then
                item.time_ms = math.max(0, mark.session_time_ms - start)
                item.review_start_ms = math.max(0, item.time_ms - mark.before_ms)
                item.review_end_ms = item.time_ms + mark.after_ms
                if finish then item.review_end_ms = math.min(item.review_end_ms, finish - start) end
                item.context_crosses_file = mark.session_time_ms - mark.before_ms < start
                    or (finish ~= nil and mark.session_time_ms + mark.after_ms > finish)
            else
                item.file_time_unknown = true
            end
            list[#list + 1] = item
        end
    end
    return {
        format = "obs-record-marks", schema_version = 1, producer_version = VERSION,
        session_id = session.id, title = session.title, started_at_utc = session.started_at_utc,
        updated_at_utc = stamp(), state = file.end_frame and "closed" or session.state,
        timing = {basis = "recorded_output_frames", fps_num = session.fps_num,
            fps_den = session.fps_den, frame_rate_divisor = session.divisor,
            quality = file.quality, encoder_latency_not_compensated = true},
        recording = {path = file.path, filename = basename(file.path), index = file.index,
            start_session_ms = start, end_session_ms = finish,
            duration_ms = file.offset_known and finish and (finish - start) or nil,
            file_offset_known = file.offset_known},
        markers = list
    }
end
local function persist(all)
    if not session then return true end
    local success = true
    for _, file in ipairs(session.files) do
        if all or file.dirty then
            if safe_write(file.sidecar, file_document(file)) then file.dirty = false
            else success = false; file.dirty = true; notify("保存失敗: " .. file.sidecar .. "（再試行できます）", true) end
        end
    end
    local files_list = array()
    for _, file in ipairs(session.files) do
        files_list[#files_list + 1] = {index = file.index, path = file.path, filename = basename(file.path),
            sidecar = file.sidecar, start_session_ms = ms(file.start_frame),
            end_session_ms = file.end_frame and ms(file.end_frame) or nil,
            file_offset_known = file.offset_known, timing_quality = file.quality}
    end
    local document = {format = "obs-record-marks-session", schema_version = 1, producer_version = VERSION,
        session_id = session.id, title = session.title, started_at_utc = session.started_at_utc,
        updated_at_utc = stamp(), state = session.state, partial_capture = session.partial_capture,
        fps_num = session.fps_num, fps_den = session.fps_den, frame_rate_divisor = session.divisor,
        files = files_list, markers = session.markers}
    if not safe_write(session.manifest, document) then
        success = false; notify("録画全体の記録を保存できません: " .. session.manifest, true)
    end
    session.unsaved = not success
    return success
end
local function add_file(path, start_frame, known, quality)
    local file = {index = #session.files + 1, path = path, start_frame = start_frame,
        offset_known = known, quality = quality, dirty = true}
    file.sidecar = reserve(path .. ".markers.json")
    session.files[#session.files + 1] = file
    return file
end
local function split_signal(cd)
    -- Output signals may run on the encoder thread. Queue only; no disk/UI work here.
    local path = obs.calldata_string(cd, "next_file")
    if path and path ~= "" then
        pending[#pending + 1] = {path = path, frame = math.max(0, frames() - 1)}
    end
end
local function drain_splits()
    if not session then pending = {}; return end
    local queue = pending; pending = {}
    for _, event in ipairs(queue) do
        local current = session.files[#session.files]
        if current.path ~= event.path then
            local boundary = math.max(current.start_frame, event.frame)
            current.end_frame = boundary; current.dirty = true
            add_file(event.path, boundary, true, "split_boundary_estimate")
        end
    end
end
local function disconnect_output()
    if handler then obs.signal_handler_disconnect(handler, "file_changed", split_signal); handler = nil end
    if output then obs.obs_output_release(output); output = nil end
end
local function start_recording(partial)
    if output then return end
    if session and session.unsaved and not persist(true) then
        notify("前の録画の未保存マークがあります。保存先の権限・空き容量を確認して再試行してください。", true)
        return
    end
    output = obs.obs_frontend_get_recording_output()
    if not output then notify("録画出力を取得できません。", true); return end
    local settings = obs.obs_output_get_settings(output)
    -- get_last_recording is a filename; get_current_record_output_path is a directory.
    local path = obs.obs_frontend_get_last_recording()
    if not path or path == "" then path = obs.obs_data_get_string(settings, "path") end
    local split_enabled = obs.obs_data_get_bool(settings, "split_file")
    obs.obs_data_release(settings)
    local video = obs.obs_video_info()
    local valid_video = obs.obs_get_video_info(video)
    if not path or path == "" or not valid_video or video.fps_num <= 0 or video.fps_den <= 0 then
        disconnect_output(); notify("録画ファイル名またはFPSを取得できません。通常のファイル録画を使用してください。", true); return
    end
    local divisor = 1
    local encoder = obs.obs_output_get_video_encoder(output)
    if encoder and obs.obs_encoder_get_frame_rate_divisor then
        divisor = math.max(1, obs.obs_encoder_get_frame_rate_divisor(encoder))
    end
    session = {id = os.date("!%Y%m%dT%H%M%SZ") .. "-" .. string.format("%.0f", obs.os_gettime_ns() % 1000000000),
        started_at_utc = stamp(), title = config.title, fps_num = video.fps_num, fps_den = video.fps_den,
        divisor = divisor, files = array(), markers = array(), state = "recording",
        partial_capture = partial == true, unsaved = false, next_id = 1}
    pending = {}
    session.manifest = reserve(path .. ".marker-session.json")
    add_file(path, 0, not partial or not split_enabled, partial and split_enabled and "unknown_prior_split" or "output_frame_estimate")
    handler = obs.obs_output_get_signal_handler(output)
    obs.signal_handler_connect(handler, "file_changed", split_signal)
    if persist(true) then
        notify(partial and "録画途中から記録を開始しました。分割済みの場合、次の分割までファイル内時刻は不明です。"
            or "録画マークの記録を開始: " .. basename(path))
    end
end
local function end_recording(reason)
    if not output then return end
    drain_splits()
    if session then
        local file = session.files[#session.files]
        file.end_frame = math.max(file.start_frame, frames()); file.dirty = true
        session.state = reason or "stopped"
        if persist(true) then notify("記録を保存しました（" .. #session.markers .. "件）: " .. session.manifest) end
    end
    disconnect_output()
end
local function add_marker(kind, debounce)
    if not obs.obs_frontend_recording_active() then notify("録画中だけマークできます。"); return false end
    if obs.obs_frontend_recording_paused() then notify("一時停止中のためマークしませんでした。"); return false end
    local now = obs.os_gettime_ns() / 1000000000
    if debounce and now - last_press < 0.35 then return false end
    if not output then start_recording(true) end
    if not session or not output then return false end
    drain_splits()
    local file = session.files[#session.files]
    local frame = math.max(0, frames() - 1)
    frame = math.max(frame, file.start_frame)
    local mark = {id = session.next_id, kind = kind, label = labels[kind], note = config.note,
        file_index = file.index, session_frame = frame, session_time_ms = ms(frame),
        before_ms = math.floor(config.before * 1000), after_ms = math.floor(config.after * 1000),
        created_at_utc = stamp()}
    session.next_id = session.next_id + 1
    session.markers[#session.markers + 1] = mark; file.dirty = true; last_press = now
    if persist(false) then
        notify(labels[kind] .. "を記録（" .. #session.markers .. "件）: " .. basename(file.path))
    end
    return true
end
local function undo_last()
    if not session or #session.markers == 0 then notify("取り消すマークがありません。"); return end
    local mark = table.remove(session.markers)
    session.files[mark.file_index].dirty = true
    if persist(false) then notify("直前のマークを取り消しました（残り" .. #session.markers .. "件）。") end
end
local function frontend_event(event)
    if event == obs.OBS_FRONTEND_EVENT_RECORDING_STARTED then start_recording(false)
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STOPPED then end_recording("stopped")
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_PAUSED then notify("録画は一時停止中です。マークは無効です。")
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_UNPAUSED then notify("録画を再開しました。")
    elseif event == obs.OBS_FRONTEND_EVENT_SCRIPTING_SHUTDOWN then end_recording("interrupted") end
end
local function timer()
    if output then
        local had_events = #pending > 0
        drain_splits()
        if had_events then persist(false) end
    end
end

function script_description()
    return "<b>OBS RecordMarks " .. VERSION .. "</b><br>見せ場・説明・完成をキーで記録する独立拡張。<br>"
        .. "設定 → ホットキーの「RecordMarks」でキーを割り当ててください。<br>"
        .. "記録は動画の隣にJSONで保存します。動画・音声・画面には追加しません。<br>"
        .. "一時停止中のマークは無効。分割境界とエンコード遅延による時刻のずれは前後再生で確認してください。"
end
function script_defaults(settings)
    obs.obs_data_set_default_int(settings, "before", 30)
    obs.obs_data_set_default_int(settings, "after", 15)
    obs.obs_data_set_default_string(settings, "note", "")
    obs.obs_data_set_default_string(settings, "title", "")
end
function script_update(settings)
    config.before = math.max(0, math.min(600, obs.obs_data_get_int(settings, "before")))
    config.after = math.max(0, math.min(600, obs.obs_data_get_int(settings, "after")))
    config.note = obs.obs_data_get_string(settings, "note")
    config.title = obs.obs_data_get_string(settings, "title")
end
function script_properties()
    local p = obs.obs_properties_create()
    obs.obs_properties_add_text(p, "status", status, obs.OBS_TEXT_INFO)
    obs.obs_properties_add_text(p, "title", "録画のテーマ（次の録画から適用）", obs.OBS_TEXT_DEFAULT)
    obs.obs_properties_add_text(p, "note", "マークに付けるメモ（以後のマークに適用）", obs.OBS_TEXT_MULTILINE)
    obs.obs_properties_add_int(p, "before", "前を確認する秒数", 0, 600, 1)
    obs.obs_properties_add_int(p, "after", "後を確認する秒数", 0, 600, 1)
    for _, kind in ipairs({"highlight", "explanation", "completion", "custom"}) do
        local chosen = kind
        obs.obs_properties_add_button(p, "add_" .. chosen, labels[chosen] .. "を記録", function()
            add_marker(chosen, false); return true
        end)
    end
    obs.obs_properties_add_button(p, "undo", "直前のマークを取り消す", function() undo_last(); return true end)
    obs.obs_properties_add_button(p, "retry", "保存を再試行", function()
        drain_splits(); if persist(true) then notify("保存を再試行しました。") end; return true
    end)
    obs.obs_properties_add_button(p, "refresh", "状態を更新", function() return true end)
    return p
end
function script_load(settings)
    script_update(settings)
    for _, kind in ipairs({"highlight", "explanation", "completion", "custom", "undo"}) do
        local chosen = kind
        callbacks[chosen] = function(pressed)
            if pressed then
                if chosen == "undo" then undo_last() else add_marker(chosen, true) end
            end
        end
        hotkeys[chosen] = obs.obs_hotkey_register_frontend("record_marks." .. chosen,
            "RecordMarks: " .. (labels[chosen] or "直前のマークを取り消す"), callbacks[chosen])
        local saved = obs.obs_data_get_array(settings, "hotkey_" .. chosen)
        if saved then obs.obs_hotkey_load(hotkeys[chosen], saved); obs.obs_data_array_release(saved) end
    end
    obs.obs_frontend_add_event_callback(frontend_event)
    obs.timer_add(timer, 250)
    if obs.obs_frontend_recording_active() then start_recording(true) end
end
function script_save(settings)
    for kind, id in pairs(hotkeys) do
        local saved = obs.obs_hotkey_save(id)
        obs.obs_data_set_array(settings, "hotkey_" .. kind, saved)
        obs.obs_data_array_release(saved)
    end
end
function script_unload()
    obs.timer_remove(timer)
    obs.obs_frontend_remove_event_callback(frontend_event)
    end_recording("interrupted")
    for _, callback in pairs(callbacks) do obs.obs_hotkey_unregister(callback) end
    hotkeys, callbacks = {}, {}
end
