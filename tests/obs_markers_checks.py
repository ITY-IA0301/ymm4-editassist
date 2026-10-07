"""Run the actual OBS Lua script in LuaJIT against an ownership-aware OBS mock."""
import json
from pathlib import Path
import tempfile
import unittest
from lupa.luajit21 import LuaRuntime

SCRIPT = Path(__file__).resolve().parents[1] / "tools/obs-markers/editassist-markers.lua"
MOCK = r'''
obslua = {LOG_INFO=1, LOG_WARNING=2, LOG_ERROR=3, OBS_PATH_DIRECTORY=1, OBS_TEXT_INFO=2,
 OBS_FRONTEND_EVENT_RECORDING_STARTED=1, OBS_FRONTEND_EVENT_RECORDING_PAUSED=2,
 OBS_FRONTEND_EVENT_RECORDING_UNPAUSED=3, OBS_FRONTEND_EVENT_RECORDING_STOPPED=4,
 OBS_FRONTEND_EVENT_SCRIPTING_SHUTDOWN=5}
local o = obslua
o.clock, o.active, o.paused, o.alive, o.output_refs = 1000000000, false, false, 0, 0
o.keys, o.events, o.timers, o.signals, o.logs = {}, {}, {}, {}, {}
o.fail_save, o.raise_save, o.loaded_bindings = false, false, 0
local function new(array)
 o.alive = o.alive + 1
 return {values={}, array=array, refs=1}
end
local function release(d)
 assert(d and d.refs > 0, "double release")
 d.refs = d.refs - 1
 if d.refs == 0 then
  o.alive = o.alive - 1
  for _,v in pairs(d.values) do
   if type(v) == "table" and v.refs then release(v) end
  end
 end
end
local function retain(d) assert(d.refs > 0); d.refs=d.refs+1; return d end
local function set(d,k,v)
 assert(d.refs > 0)
 local old=d.values[k]
 if type(old)=="table" and old.refs then release(old) end
 if type(v)=="table" and v.refs then retain(v) end
 d.values[k]=v
end
o.obs_data_create=function() return new(false) end
o.obs_data_array_create=function() return new(true) end
o.obs_data_release=release
o.obs_data_array_release=release
o.obs_data_set_string=set
o.obs_data_set_double=set
o.obs_data_set_bool=set
o.obs_data_set_int=set
o.obs_data_set_obj=set
o.obs_data_set_array=set
o.obs_data_array_push_back=function(d,v) set(d,#d.values+1,v) end
o.obs_data_get_string=function(d,k) return d.values[k] or "" end
o.obs_data_get_int=function(d,k) return d.values[k] or 0 end
o.obs_data_get_array=function(d,k) if d.values[k] then return retain(d.values[k]) end end
o.obs_data_set_default_int=function(d,k,v) if d.values[k]==nil then set(d,k,v) end end
o.obs_data_set_default_string=o.obs_data_set_default_int
o.obs_data_save_json_safe=function(d,path,tmp,bak)
 if o.raise_save then error("injected save exception") end
 if o.fail_save then return false end
 return python_save(d,path,tmp,bak)
end
o.os_gettime_ns=function() return o.clock end
o.script_log=function(level,text) o.logs[#o.logs+1]={level=level,text=text} end
o.obs_frontend_recording_active=function() return o.active end
o.obs_frontend_recording_paused=function() return o.paused end
o.obs_frontend_get_recording_output=function() o.output_refs=o.output_refs+1; return "output" end
o.obs_output_release=function() o.output_refs=o.output_refs-1; assert(o.output_refs>=0) end
o.obs_output_get_settings=function()
 local d=new(false); d.values.path=o.path; return d
end
o.obs_output_get_signal_handler=function() return "handler" end
o.signal_handler_connect=function(h,name,cb) o.signals[name]=cb end
o.signal_handler_disconnect=function(h,name,cb) assert(o.signals[name]==cb); o.signals[name]=nil end
o.calldata_string=function(d,k) return d[k] end
o.obs_frontend_add_event_callback=function(cb) o.events[cb]=true end
o.obs_frontend_remove_event_callback=function(cb) o.events[cb]=nil end
o.timer_add=function(cb,ms) o.timers[cb]=true end
o.timer_remove=function(cb) o.timers[cb]=nil end
o.obs_hotkey_register_frontend=function(name,description,cb) o.keys[name]=cb; return name end
o.obs_hotkey_unregister=function(cb)
 for name,value in pairs(o.keys) do if value==cb then o.keys[name]=nil end end
end
o.obs_hotkey_save=function(id)
 local a=new(true); local d=new(false); d.values.key="OBS_KEY_F9"
 set(a,1,d); release(d); return a
end
o.obs_hotkey_load=function(id,a) o.loaded_bindings=o.loaded_bindings+1 end
o.obs_properties_create=function() return {} end
o.obs_properties_add_int=function() end
o.obs_properties_add_path=function() end
o.obs_properties_add_text=function() end
function advance(seconds) o.clock=o.clock+seconds*1000000000 end
function event(value)
 if value==1 then o.active=true; o.paused=false
 elseif value==2 then o.paused=true
 elseif value==3 then o.paused=false
 elseif value==4 then o.active=false; o.paused=false end
 for cb in pairs(o.events) do cb(value) end
end
function key(category,pressed) o.keys["editassist_marker_"..category](pressed) end
function press(category) key(category,true); key(category,false) end
function tick() for cb in pairs(o.timers) do cb() end end
function split(path) o.path=path; o.signals.file_changed({next_file=path}) end
function registrations()
 local n=0; for _ in pairs(o.events) do n=n+1 end
 for _ in pairs(o.keys) do n=n+1 end
 for _ in pairs(o.timers) do n=n+1 end
 for _ in pairs(o.signals) do n=n+1 end
 return n
end
'''

def plain(data):
    values = data["values"]
    if data["array"]:
        return [plain(values[i]) for i in range(1, len(values)+1)]
    return {k: plain(v) if hasattr(v, "items") else v for k, v in values.items()}

class MarkerChecks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name) / "日本語の録画"
        self.root.mkdir()
        self.video = self.root / "酒場と宿屋.mp4"
        self.video.write_bytes(b"original video must remain untouched")
        self.original = self.video.read_bytes()
        self.saved = {}
        self.save_count = 0
        self.lua = LuaRuntime(unpack_returned_tuples=True)
        self.lua.globals().python_save = self.save
        self.lua.execute(MOCK)
        self.obs = self.lua.globals().obslua
        self.obs.path = str(self.video)
        self.lua.execute(SCRIPT.read_text(encoding="utf-8"))
        self.settings = self.obs.obs_data_create()
        self.lua.globals().script_defaults(self.settings)
        self.load()
    def load(self):
        self.lua.globals().script_load(self.settings)
    def save(self, data, path, temporary, backup):
        doc = plain(data)
        target = Path(path)
        tmp = Path(path+"."+temporary)
        tmp.write_text(json.dumps(doc,ensure_ascii=False,allow_nan=False),encoding="utf-8")
        if target.exists():
            target.replace(Path(path+"."+backup))
        tmp.replace(target)
        self.saved[str(target)] = json.loads(target.read_text(encoding="utf-8"))
        self.save_count += 1
        return True
    def call(self, name, *args): return self.lua.globals()[name](*args)
    def doc(self): return list(self.saved.values())[-1]
    def start(self): self.call("event",1)
    def press(self, category): self.call("press",category)
    def advance(self, seconds): self.call("advance",seconds)
    def tearDown(self):
        self.call("script_unload")
        self.obs.obs_data_release(self.settings)
        self.assertEqual(self.obs.alive,0,"OBS data references leaked")
        self.assertEqual(self.obs.output_refs,0)
        self.assertEqual(self.call("registrations"),0)
        self.assertEqual(self.video.read_bytes(),self.original)
        self.tmp.cleanup()

    def test_categories_context_and_json(self):
        self.start()
        for kind in ["highlight","explanation","completed"]:
            self.advance(10); self.press(kind)
        d=self.doc()
        self.assertEqual([x["category"] for x in d["markers"]],["highlight","explanation","completed"])
        self.assertEqual([x["position_seconds"] for x in d["markers"]],[10,20,30])
        self.assertEqual(d["markers"][0]["context"]["recording_start_seconds"],0)
        self.assertEqual(d["markers"][2]["context"]["recording_end_seconds"],45)
        self.assertEqual(d["schema"],"EditAssist.RecordingMarkers")
        self.assertEqual(d["schema_version"],1)
        self.assertTrue(d["timing_is_approximate"])
        self.assertEqual(d["segments"][0]["video_file"],str(self.video))
        self.assertTrue(any(self.root.glob("*.bak")))

    def test_hold_and_outside_recording(self):
        self.press("highlight")
        self.assertFalse(self.saved)
        self.start()
        self.call("key","highlight",True)
        for _ in range(10): self.call("key","highlight",True)
        self.assertEqual(len(self.doc()["markers"]),1)
        self.call("key","highlight",False)
        self.press("highlight")
        self.assertEqual(len(self.doc()["markers"]),2)
        self.call("event",4)
        self.press("completed")
        self.assertEqual(len(self.doc()["markers"]),2)

    def test_pause_resume_and_undo(self):
        self.start(); self.advance(10); self.press("highlight")
        self.call("event",2); self.advance(120); self.press("explanation")
        self.assertEqual(len(self.doc()["markers"]),1)
        self.press("undo")
        self.assertEqual(self.doc()["markers"],[])
        self.call("event",3); self.advance(5); self.press("completed")
        self.assertEqual(self.doc()["markers"][0]["position_seconds"],15)
        self.assertEqual(self.doc()["markers"][0]["id"],2)
        self.call("event",4)
        self.assertEqual(self.doc()["recording_duration_seconds"],15)

    def test_stop_while_paused(self):
        self.start(); self.advance(15); self.call("event",2); self.advance(300)
        self.call("event",4)
        self.assertEqual(self.doc()["recording_duration_seconds"],15)
        self.assertEqual(self.doc()["state"],"stopped")

    def test_split_deferred_and_cross_file_context(self):
        self.start(); self.advance(60); self.press("highlight")
        count=self.save_count
        next_file=str(self.root/"録画2.mp4")
        self.call("split",next_file)
        self.assertEqual(self.save_count,count,"disk I/O in encoder callback")
        self.advance(5); self.press("completed")
        d=self.doc()
        self.assertEqual(len(d["segments"]),2)
        self.assertEqual(d["segments"][0]["recording_end_seconds"],60)
        m=d["markers"][-1]
        self.assertEqual(m["video_file"],next_file)
        self.assertEqual(m["position_seconds"],5)
        self.assertEqual(m["recording_position_seconds"],65)
        self.assertEqual(m["context"]["recording_start_seconds"],35)

    def test_split_timestamp_survives_delayed_processing(self):
        self.start(); self.advance(10)
        self.call("split",str(self.root/"録画2.mp4"))
        self.advance(10); self.call("event",2)
        self.advance(60); self.call("event",3)
        self.advance(5); self.call("tick")
        self.assertEqual(self.doc()["segments"][1]["recording_start_seconds"],10)
        self.press("completed")
        self.assertEqual(self.doc()["markers"][-1]["position_seconds"],15)

    def test_failure_retry_and_pending_sessions(self):
        self.start(); self.advance(10)
        self.obs.fail_save=True
        self.press("highlight"); self.call("event",4)
        first_path=next(iter(self.saved))
        self.assertEqual(self.saved[first_path]["markers"],[])
        self.advance(1); self.start(); self.advance(2); self.press("completed")
        self.obs.fail_save=False; self.call("tick")
        self.assertEqual(len(self.saved),2)
        self.assertEqual(self.saved[first_path]["markers"][0]["position_seconds"],10)
        self.assertEqual(self.saved[first_path]["state"],"stopped")
        newest=[d for p,d in self.saved.items() if p!=first_path][0]
        self.assertEqual(newest["markers"][0]["position_seconds"],2)

    def test_save_exception_releases_all_references(self):
        self.start()
        baseline=self.obs.alive
        self.obs.raise_save=True
        self.press("highlight")
        self.assertEqual(self.obs.alive,baseline)
        self.obs.raise_save=False; self.call("tick")
        self.assertEqual(len(self.doc()["markers"]),1)

    def test_ten_one_hour_recordings(self):
        for i in range(10):
            self.obs.path=str(self.root/("recording-%02d.mp4"%i))
            self.start(); self.advance(3599); self.press("highlight")
            self.advance(1); self.call("event",4)
        self.assertEqual(len(self.saved),10)
        self.assertEqual(len({d["session_id"] for d in self.saved.values()}),10)
        self.assertTrue(all(d["recording_duration_seconds"]==3600 for d in self.saved.values()))
        self.assertTrue(all(d["markers"][0]["position_seconds"]==3599 for d in self.saved.values()))

    def test_key_settings_reload_and_no_duplicate_registration(self):
        self.load()
        self.assertEqual(self.call("registrations"),6)
        self.call("script_save",self.settings)
        self.call("script_unload")
        self.assertEqual(self.call("registrations"),0)
        self.load()
        self.assertEqual(self.obs.loaded_bindings,4)
        self.assertEqual(self.call("registrations"),6)

    def test_mid_recording_install_waits_for_next_recording(self):
        self.call("script_unload")
        self.obs.active=True
        self.load(); self.advance(50); self.press("highlight")
        self.assertFalse(self.saved)
        self.call("event",4); self.start(); self.advance(2); self.press("highlight")
        self.assertEqual(self.doc()["markers"][0]["position_seconds"],2)

    def test_custom_directory_and_settings_apply_next_recording(self):
        destination=self.root/"マーク"; destination.mkdir()
        self.obs.obs_data_set_string(self.settings,"output_directory",str(destination))
        self.obs.obs_data_set_int(self.settings,"before_seconds",10)
        self.call("script_update",self.settings)
        self.start(); self.advance(20)
        self.obs.obs_data_set_int(self.settings,"before_seconds",90)
        self.call("script_update",self.settings)
        self.press("highlight")
        self.assertEqual(Path(next(iter(self.saved))).parent,destination)
        self.assertEqual(self.doc()["markers"][0]["context"]["before_seconds"],10)

    def test_unknown_output_and_missing_api(self):
        self.obs.path=""
        self.start(); self.press("highlight")
        self.assertFalse(self.saved)
        self.assertEqual(self.obs.output_refs,0)
        self.call("event",4)
        self.obs.path=str(self.video)
        self.obs.obs_data_save_json_safe=None
        self.start()
        self.assertFalse(self.saved)
        self.assertEqual(self.obs.output_refs,0)

    def test_unload_active_recording(self):
        self.start(); self.advance(8); self.press("completed")
        self.call("script_unload")
        self.assertEqual(self.doc()["state"],"script_unloaded")
        self.assertEqual(self.doc()["recording_duration_seconds"],8)
        self.assertEqual(self.call("registrations"),0)

    def test_shutdown_and_empty_recording(self):
        self.start(); self.advance(5); self.call("event",5)
        self.assertEqual(self.doc()["state"],"obs_shutdown")
        self.assertEqual(self.doc()["markers"],[])
        self.assertEqual(self.doc()["recording_duration_seconds"],5)

    def test_properties_and_description(self):
        self.assertIn("0.1.0",self.call("script_description"))
        self.assertIsNotNone(self.call("script_properties"))

if __name__ == "__main__":
    unittest.main(verbosity=2)
