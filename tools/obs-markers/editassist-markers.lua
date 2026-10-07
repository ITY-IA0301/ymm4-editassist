-- EditAssist OBS Markers 0.1.0. OBS: Tools > Scripts.
local obs = obslua
local VERSION = "0.1.0"
local session, output, handler
local pending, splits, hotkeys = {}, {}, {}
local config = {before = 30, after = 15, directory = ""}
local sequence, running, status = 0, false, "録画待ち"

local function log(level, text)
    obs.script_log(level, "[EditAssist Markers] " .. text)
end
local function now() return obs.os_gettime_ns() end
local function utc() return os.date("!%Y-%m-%dT%H:%M:%SZ") end
local function basename(path) return path:match("([^/\\]+)$") or path end
local function join(directory, name)
    local sep = directory:find("\\", 1, true) and "\\" or "/"
    return directory:gsub("[/\\]+$", "") .. sep .. name
end

-- Each OBS allocation owns a reference; release all even when saving throws.
-- Use OBS's UTF-8 file APIs, including for Japanese Windows paths.
local function save(s)
    local owned = {}
    local function object(values)
        local data = obs.obs_data_create()
        owned[#owned + 1] = {data, false}
        for key, value in pairs(values) do
            local kind = type(value)
            if kind == "string" then obs.obs_data_set_string(data, key, value)
            elseif kind == "number" then obs.obs_data_set_double(data, key, value)
            elseif kind == "boolean" then obs.obs_data_set_bool(data, key, value)
            elseif kind == "table" then
                if key == "markers" or key == "segments" then
                    local array = obs.obs_data_array_create()
                    owned[#owned + 1] = {array, true}
                    for _, item in ipairs(value) do
                        obs.obs_data_array_push_back(array, object(item))
                    end
                    obs.obs_data_set_array(data, key, array)
                else obs.obs_data_set_obj(data, key, object(value)) end
            end
        end
        return data
    end
    local ok, result = pcall(function()
        return obs.obs_data_save_json_safe(object(s.document), s.path, "tmp", "bak")
    end)
    for i = #owned, 1, -1 do
        if owned[i][2] then obs.obs_data_array_release(owned[i][1])
        else obs.obs_data_release(owned[i][1]) end
    end
    if ok and result then
        s.dirty = false
        if s.failed then log(obs.LOG_INFO, "保存を再開しました：" .. s.path) end
        s.failed = false
        return true
    end
    if not s.failed then
        log(obs.LOG_WARNING, "保存できません。記録をメモリに保持して再試行します。OBSを閉じる前に保存先を確認してください：" .. s.path)
    end
    s.failed, status = true, "保存エラー：OBSログと保存先を確認してください"
    return false
end
local function persist(s) s.dirty = true; return save(s) end
local function elapsed(s, at)
    return math.max(0, (math.min(s.pause_at or at, at) - s.started_ns - s.paused_ns) / 1000000000)
end

-- File-change callbacks can run on an encoder thread. No disk I/O here.
local function on_split(cd)
    local path = obs.calldata_string(cd, "next_file")
    if session and path and path ~= "" then
        splits[#splits + 1] = {path = path, at = elapsed(session, now())}
    end
end
local function drain_splits()
    if not session then splits = {}; return end
    local changed = false
    for _, event in ipairs(splits) do
        local segments = session.document.segments
        local previous = segments[#segments]
        if event.path ~= previous.video_file then
            local boundary = math.max(previous.recording_start_seconds, event.at)
            previous.recording_end_seconds = boundary
            segments[#segments + 1] = {
                index = #segments + 1, video_file = event.path,
                recording_start_seconds = boundary, boundary_is_approximate = true,
            }
            changed = true
        end
    end
    splits = {}
    if changed then persist(session) end
end
local function disconnect()
    if handler then obs.signal_handler_disconnect(handler, "file_changed", on_split); handler = nil end
    if output then obs.obs_output_release(output); output = nil end
end
local function finish(reason)
    if not session then disconnect(); return end
    drain_splits()
    local at = elapsed(session, now())
    local doc = session.document
    doc.segments[#doc.segments].recording_end_seconds = at
    doc.recording_duration_seconds, doc.finished_at_utc, doc.state = at, utc(), reason
    if persist(session) then
        status = "保存完了：" .. basename(session.path)
        log(obs.LOG_INFO, "録画マークを保存しました：" .. session.path)
    else pending[#pending + 1] = session end
    session, splits = nil, {}
    disconnect()
end
local function start()
    if session then return end
    for _, name in ipairs({"os_gettime_ns", "obs_frontend_get_recording_output",
        "obs_output_get_settings", "obs_data_save_json_safe"}) do
        if not obs[name] then
            status = "このOBSでは必要なAPIが使えません：" .. name
            log(obs.LOG_WARNING, status)
            return
        end
    end
    output = obs.obs_frontend_get_recording_output()
    if not output then log(obs.LOG_WARNING, "録画出力が取得できません"); return end
    local settings = obs.obs_output_get_settings(output)
    local path = obs.obs_data_get_string(settings, "path") or ""
    obs.obs_data_release(settings)
    if path == "" then
        status = "録画ファイルのパスが取得できません。標準録画出力を使用してください"
        log(obs.LOG_WARNING, status)
        disconnect()
        return
    end
    sequence = sequence + 1
    local started = now()
    local id = os.date("!%Y%m%dT%H%M%SZ") .. "-" .. string.format("%.0f", started) .. "-" .. sequence
    local filename = basename(path) .. ".editassist-" .. id .. ".markers.json"
    local destination = path .. ".editassist-" .. id .. ".markers.json"
    if config.directory ~= "" then destination = join(config.directory, filename) end
    session = {
        path = destination, started_ns = started, paused_ns = 0,
        before = config.before, after = config.after, dirty = true,
        document = {
            schema = "EditAssist.RecordingMarkers", schema_version = 1,
            producer = "EditAssist OBS Markers", producer_version = VERSION,
            session_id = id, started_at_utc = utc(), state = "recording",
            timing_method = "monotonic_recording_events", timing_is_approximate = true,
            segments = {{index = 1, video_file = path, recording_start_seconds = 0,
                boundary_is_approximate = true}}, markers = {},
        },
    }
    handler = obs.obs_output_get_signal_handler(output)
    obs.signal_handler_connect(handler, "file_changed", on_split)
    persist(session)
    if not session.failed then status = "録画中：マーク待ち" end
    log(obs.LOG_INFO, "録画マークを開始しました：" .. destination)
end
local function pause()
    if session and not session.pause_at then
        session.pause_at, session.document.state = now(), "paused"
        persist(session)
    end
end
local function resume()
    if session and session.pause_at then
        session.paused_ns = session.paused_ns + now() - session.pause_at
        session.pause_at, session.document.state = nil, "recording"
        persist(session)
    end
end
local function mark(category, label)
    if not session or not obs.obs_frontend_recording_active() then return end
    if session.pause_at or obs.obs_frontend_recording_paused() then
        log(obs.LOG_INFO, "一時停止中のため、マークを追加しませんでした")
        return
    end
    drain_splits()
    local at = elapsed(session, now())
    local segments = session.document.segments
    local segment = segments[#segments]
    local position = math.max(0, at - segment.recording_start_seconds)
    session.marker_sequence = (session.marker_sequence or 0) + 1
    local markers = session.document.markers
    markers[#markers + 1] = {
        id = session.marker_sequence, category = category, label = label,
        segment_index = segment.index, video_file = segment.video_file,
        position_seconds = position, recording_position_seconds = at,
        created_at_utc = utc(), timing_is_approximate = true,
        context = {before_seconds = session.before, after_seconds = session.after,
            recording_start_seconds = math.max(0, at - session.before),
            recording_end_seconds = at + session.after},
    }
    if persist(session) then
        status = label .. "を保存：" .. string.format("%.1f秒", position)
        log(obs.LOG_INFO, status .. " / " .. basename(segment.video_file))
    end
end
local function undo()
    if not session then return end
    if table.remove(session.document.markers) and persist(session) then
        status = "直前のマークを取り消しました"
        log(obs.LOG_INFO, status)
    end
end
local function on_event(event)
    if event == obs.OBS_FRONTEND_EVENT_RECORDING_STARTED then start()
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_PAUSED then pause()
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_UNPAUSED then resume()
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STOPPED then finish("stopped")
    elseif event == obs.OBS_FRONTEND_EVENT_SCRIPTING_SHUTDOWN then finish("obs_shutdown") end
end
local function tick()
    drain_splits()
    if session and session.dirty then save(session) end
    for i = #pending, 1, -1 do
        if save(pending[i]) then table.remove(pending, i) end
    end
end

function script_description()
    return "<b>EditAssist OBS Markers " .. VERSION .. "</b><br>録画中の見せ場・説明・完成をキーで記録します。"
        .. "<br>設定 → ホットキーで「EditAssist」を検索してキーを割り当ててください。"
        .. "<br>動画の隣にJSONを保存します。時刻は場面を探すための目安です。"
        .. "<br>導入後は録画を停止してから、新しく録画を開始してください。"
end
function script_defaults(settings)
    obs.obs_data_set_default_int(settings, "before_seconds", 30)
    obs.obs_data_set_default_int(settings, "after_seconds", 15)
    obs.obs_data_set_default_string(settings, "output_directory", "")
end
function script_update(settings)
    config.before = math.max(0, math.min(300, obs.obs_data_get_int(settings, "before_seconds")))
    config.after = math.max(0, math.min(300, obs.obs_data_get_int(settings, "after_seconds")))
    config.directory = obs.obs_data_get_string(settings, "output_directory") or ""
end
function script_properties()
    local props = obs.obs_properties_create()
    obs.obs_properties_add_int(props, "before_seconds", "マークの何秒前から確認するか", 0, 300, 1)
    obs.obs_properties_add_int(props, "after_seconds", "マークの何秒後まで確認するか", 0, 300, 1)
    obs.obs_properties_add_path(props, "output_directory", "保存先（空欄なら動画の隣）", obs.OBS_PATH_DIRECTORY, "", nil)
    obs.obs_properties_add_text(props, "status", status, obs.OBS_TEXT_INFO)
    return props
end
local function register_key(settings, name, description, action)
    local entry = {name = name, down = false}
    entry.callback = function(pressed)
        if pressed and not entry.down then action() end
        entry.down = pressed
    end
    entry.id = obs.obs_hotkey_register_frontend(name, description, entry.callback)
    local bindings = obs.obs_data_get_array(settings, name)
    if bindings then
        obs.obs_hotkey_load(entry.id, bindings)
        obs.obs_data_array_release(bindings)
    end
    hotkeys[#hotkeys + 1] = entry
end
function script_load(settings)
    if running then return end
    running = true
    script_update(settings)
    register_key(settings, "editassist_marker_highlight", "EditAssist：見せ場を記録",
        function() mark("highlight", "見せ場") end)
    register_key(settings, "editassist_marker_explanation", "EditAssist：説明場面を記録",
        function() mark("explanation", "説明") end)
    register_key(settings, "editassist_marker_completed", "EditAssist：完成場面を記録",
        function() mark("completed", "完成") end)
    register_key(settings, "editassist_marker_undo", "EditAssist：直前のマークを取り消す", undo)
    obs.obs_frontend_add_event_callback(on_event)
    obs.timer_add(tick, 1000)
    if obs.obs_frontend_recording_active() then
        status = "次の録画開始から記録します（現在の録画は途中導入のため対象外）"
        log(obs.LOG_WARNING, status)
    end
end
function script_save(settings)
    for _, entry in ipairs(hotkeys) do
        local bindings = obs.obs_hotkey_save(entry.id)
        obs.obs_data_set_array(settings, entry.name, bindings)
        obs.obs_data_array_release(bindings)
    end
end
function script_unload()
    if not running then return end
    finish("script_unloaded")
    tick()
    if #pending > 0 then
        log(obs.LOG_ERROR, "未保存の記録があります。スクリプト終了後はメモリから復旧できません。保存先を確認してください")
    end
    obs.timer_remove(tick)
    obs.obs_frontend_remove_event_callback(on_event)
    for _, entry in ipairs(hotkeys) do obs.obs_hotkey_unregister(entry.callback) end
    hotkeys, running = {}, false
end
