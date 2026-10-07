-- Executes the public OBS script callbacks against a deterministic OBS API stub.
local root, outdir = TEST_ROOT, TEST_OUTPUT
local passed, callbacks, hotkeys, buttons, saved, occupied = 0, {}, {}, {}, {}, {}
local active, paused, count, current_path = false, false, 0, "C:/録画/01.mkv"
local fps_num, fps_den, divisor, split_enabled, fail = 60, 1, 1, false, false
local clock, refcount, writes, timer_callback, signal_callback, logs = 1000000000, 0, 0, nil, nil, {}
local o = {
    LOG_INFO=200, LOG_ERROR=100, OBS_TEXT_INFO=2, OBS_TEXT_DEFAULT=0, OBS_TEXT_MULTILINE=1,
    OBS_FRONTEND_EVENT_RECORDING_STARTED=1, OBS_FRONTEND_EVENT_RECORDING_STOPPED=2,
    OBS_FRONTEND_EVENT_RECORDING_PAUSED=3, OBS_FRONTEND_EVENT_RECORDING_UNPAUSED=4,
    OBS_FRONTEND_EVENT_SCRIPTING_SHUTDOWN=5
}
obslua=o
function o.script_log(level,text) logs[#logs+1]=text end
function o.os_gettime_ns()return clock end
function o.os_file_exists(path)return saved[path]~=nil or occupied[path]==true end
function o.obs_data_create_from_json(text)return {encoded=text} end
function o.obs_data_save_json_safe(data,path,tmp,bak)
    assert(tmp=='tmp' and bak=='bak');writes=writes+1
    if fail then return false end
    if saved[path] then saved[path..'.bak']=saved[path] end
    saved[path]=data.encoded;return true
end
function o.obs_data_create_from_json_file(path)return saved[path] and {encoded=saved[path]} or nil end
function o.obs_data_release(data)end
function o.obs_data_get_string(data,key)return data[key] or '' end
function o.obs_data_get_int(data,key)return data[key] or 0 end
function o.obs_data_get_bool(data,key)return data[key]==true end
function o.obs_data_set_default_int(data,key,value)if data[key]==nil then data[key]=value end end
o.obs_data_set_default_string=o.obs_data_set_default_int
function o.obs_frontend_recording_active()return active end
function o.obs_frontend_recording_paused()return paused end
function o.obs_frontend_get_recording_output()if active then refcount=refcount+1;return {} end end
function o.obs_output_release(output)refcount=refcount-1;assert(refcount>=0)end
function o.obs_output_get_settings(output)return {path=current_path,split_file=split_enabled} end
function o.obs_frontend_get_last_recording()return current_path end
function o.obs_video_info()return {} end
function o.obs_get_video_info(info)info.fps_num=fps_num;info.fps_den=fps_den;return true end
function o.obs_output_get_video_encoder(output)return {} end
function o.obs_encoder_get_frame_rate_divisor(encoder)return divisor end
function o.obs_output_get_signal_handler(output)return {} end
function o.signal_handler_connect(handler,name,callback)assert(name=='file_changed');signal_callback=callback end
function o.signal_handler_disconnect(handler,name,callback)assert(signal_callback==callback);signal_callback=nil end
function o.calldata_string(cd,key)return cd[key] end
function o.obs_output_get_total_frames(output)return count end
function o.obs_frontend_add_event_callback(callback)callbacks.frontend=callback end
function o.obs_frontend_remove_event_callback(callback)assert(callbacks.frontend==callback);callbacks.frontend=nil end
function o.timer_add(callback,interval)assert(interval==250);timer_callback=callback end
function o.timer_remove(callback)assert(timer_callback==callback);timer_callback=nil end
function o.obs_hotkey_register_frontend(name,label,callback)hotkeys[name]=callback;return name end
function o.obs_hotkey_unregister(callback)for key,value in pairs(hotkeys)do if value==callback then hotkeys[key]=nil end end end
function o.obs_hotkey_save(id)return {id} end
function o.obs_hotkey_load(id,data)assert(data[1]==id)end
function o.obs_data_get_array(data,key)return data[key]end
function o.obs_data_set_array(data,key,value)data[key]=value end
function o.obs_data_array_release(data)end
function o.obs_properties_create()buttons={};return {} end
function o.obs_properties_add_text(p,name,label,kind)end
function o.obs_properties_add_int(p,name,label,a,b,step)end
function o.obs_properties_add_button(p,name,label,callback)buttons[name]=callback end
dofile(root..'/record-marks.lua')
local settings={}
script_defaults(settings);script_load(settings);script_properties()
local function check(name,condition)assert(condition,name);passed=passed+1;print('PASS '..name)end
local function press(kind)
    clock=clock+1000000000
    hotkeys['record_marks.'..kind](true)
end
local function start(path)
    current_path=path;count=0;active=true;paused=false
    callbacks.frontend(o.OBS_FRONTEND_EVENT_RECORDING_STARTED)
end
local function stop()
    active=false;callbacks.frontend(o.OBS_FRONTEND_EVENT_RECORDING_STOPPED)
end
local function text(path)return assert(saved[path],path)end
local function contains(path,pattern)return text(path):find(pattern,1,true)~=nil end

press('highlight');check('not recording: no files created',writes==0)
start('C:/録画/01.mkv')
check('empty marker list is an array',contains(current_path..'.markers.json','"markers":[]'))
check('one output reference held while recording',refcount==1)
count=601;press('highlight')
check('ten-second mark matches last output frame',contains(current_path..'.markers.json','"time_ms":10000'))
check('pre-context is clamped at start',contains(current_path..'.markers.json','"review_start_ms":0'))
local oldwrites=writes
hotkeys['record_marks.highlight'](true);check('repeat keypress is debounced',writes==oldwrites)
hotkeys['record_marks.highlight'](false);check('key release does not mark',writes==oldwrites)
paused=true;callbacks.frontend(o.OBS_FRONTEND_EVENT_RECORDING_PAUSED);press('completion')
check('paused recording rejects marker',writes==oldwrites)
paused=false;callbacks.frontend(o.OBS_FRONTEND_EVENT_RECORDING_UNPAUSED)
settings.note='屋根を壊した\n"テスト"\\path\t終わり';script_update(settings)
count=1201;press('explanation')
check('resumed mark uses output time, excludes wall-time pause',contains(current_path..'.markers.json','"time_ms":20000'))
check('JSON string escapes newlines and quotes',contains(current_path..'.markers.json','屋根を壊した\\n\\"テスト\\"\\\\path\\t終わり'))
count=1800;stop()
check('final context is clamped at file end',contains(current_path..'.markers.json','"review_end_ms":30000'))
check('references and split handler released after stop',refcount==0 and signal_callback==nil)
check('previous JSON is kept as backup',saved['C:/録画/01.mkv.markers.json.bak']~=nil)
check('session records stopped state',contains('C:/録画/01.mkv.marker-session.json','"state":"stopped"'))
press('undo');check('undo after stop removes last marker',not contains('C:/録画/01.mkv.markers.json','"kind":"explanation"'))

split_enabled=true;start('C:/録画/split-1.mkv')
count=3001;press('highlight')
count=3601;local beforewrites=writes
signal_callback({next_file='C:/録画/split-2.mkv'});current_path='C:/録画/split-2.mkv'
check('encoder callback does not write files',writes==beforewrites)
count=3901;press('completion')
check('hotkey drains queued split before marking',contains(current_path..'.markers.json','"file_index":2'))
check('split-relative mark is five seconds',contains(current_path..'.markers.json','"time_ms":5000'))
check('split mark preserves session timestamp',contains(current_path..'.markers.json','"session_time_ms":65000'))
check('split is marked as an estimate',contains(current_path..'.markers.json','"quality":"split_boundary_estimate"'))
check('pre-context crossing boundary is flagged',contains(current_path..'.markers.json','"context_crosses_file":true'))
count=7201;signal_callback({next_file='C:/録画/split-3.mkv'});current_path='C:/録画/split-3.mkv';timer_callback()
check('timer creates next-file empty sidecar',contains(current_path..'.markers.json','"markers":[]'))
count=7300;stop()
check('manifest retains all split videos',contains('C:/録画/split-1.mkv.marker-session.json','split-3.mkv'))

split_enabled=false;fps_num=30000;fps_den=1001;divisor=2
start('C:/録画/fractional.mp4');count=151;press('custom');stop()
check('fractional fps and divisor are honored',contains(current_path..'.markers.json','"time_ms":10010'))
fps_num=60;fps_den=1;divisor=1
start('C:/録画/ten-hours.mkv');count=2160001;press('highlight');stop()
check('ten-hour recording does not overflow clock',contains(current_path..'.markers.json','"time_ms":36000000'))

local original=saved['C:/録画/01.mkv.markers.json']
start('C:/録画/01.mkv');count=200;press('custom');stop()
check('new session does not overwrite existing sidecar',saved['C:/録画/01.mkv.markers.json']==original)
local collision=false
for path in pairs(saved)do if path:match('01%.mkv%.markers%.')then collision=true end end
check('new session uses suffixed sidecar',collision)

start('C:/録画/failure.mkv');fail=true;count=600;press('highlight')
check('save failure is reported',logs[#logs]:find('保存',1,true)~=nil)
stop();local old=writes
start('C:/録画/next.mkv')
check('unsaved previous session blocks replacement',refcount==0 and saved['C:/録画/next.mkv.markers.json']==nil)
fail=false;buttons.retry();count=900;press('custom')
check('retry preserves original unsaved marker',contains('C:/録画/failure.mkv.markers.json','"kind":"highlight"'))
check('recording reconnects after successful recovery',refcount==1)
stop()

-- Reload in the middle of a recording with an unknown earlier split.
script_unload();check('unload removes all callbacks and hotkeys',next(hotkeys)==nil and callbacks.frontend==nil and timer_callback==nil)
active=true;split_enabled=true;current_path='C:/録画/late-5.mkv';count=5000
script_load(settings);script_properties();press('highlight')
check('late attachment does not invent file timestamp',not contains(current_path..'.markers.json','"time_ms":'))
check('late attachment marks offset as unknown',contains(current_path..'.markers.json','"file_offset_known":false'))
check('late attachment has session clock',contains(current_path..'.markers.json','"session_time_ms":'))
count=6001;signal_callback({next_file='C:/録画/late-6.mkv'});current_path='C:/録画/late-6.mkv'
count=6601;press('custom')
check('observed split restores relative timestamp',contains(current_path..'.markers.json','"time_ms":10000'))
script_save(settings)
check('hotkey bindings persisted',settings.hotkey_highlight[1]=='record_marks.highlight')
stop();script_unload();script_load(settings)
check('hotkey bindings restore without duplicates',hotkeys['record_marks.highlight']~=nil)
script_properties();buttons.undo();check('undo updates disk snapshot',not contains('C:/録画/late-6.mkv.markers.json','"kind":"custom"'))

start('C:/録画/shutdown.mkv');count=100;press('highlight')
callbacks.frontend(o.OBS_FRONTEND_EVENT_SCRIPTING_SHUTDOWN)
check('shutdown flushes interrupted session',contains('C:/録画/shutdown.mkv.marker-session.json','"state":"interrupted"'))
script_unload();check('no output reference leaks at end',refcount==0)

local number=0
for path,document in pairs(saved)do
    if not path:match('%.bak$')then
        number=number+1
        local f=assert(io.open(outdir..'/snapshot-'..number..'.json','wb'));f:write(document);f:close()
    end
end
print('Lua assertions: '..passed..'; JSON snapshots: '..number)
TEST_PASSED=passed
