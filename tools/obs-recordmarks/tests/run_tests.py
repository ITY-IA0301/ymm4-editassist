"""Run meaningful OBS lifecycle tests with the system Lua library, then validate JSON.

This is an OBS API mock, not an OBS/Windows integration test. No Python dependency
is required by the extension itself. The runner supports liblua5.4 or liblua5.3.
"""
import ctypes
import ctypes.util
import json
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
library = ctypes.util.find_library('lua5.4') or ctypes.util.find_library('lua5.3')
if not library:
    raise SystemExit('Tests require liblua5.4 or liblua5.3. The OBS extension does not.')
lua = ctypes.CDLL(library)
lua.luaL_newstate.restype = ctypes.c_void_p
lua.luaL_openlibs.argtypes = [ctypes.c_void_p]
lua.luaL_loadstring.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
lua.luaL_loadstring.restype = ctypes.c_int
lua.lua_pcallk.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_longlong, ctypes.c_void_p]
lua.lua_pcallk.restype = ctypes.c_int
lua.lua_tolstring.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_size_t)]
lua.lua_tolstring.restype = ctypes.c_char_p
lua.lua_close.argtypes = [ctypes.c_void_p]
state = lua.luaL_newstate()
lua.luaL_openlibs(state)
try:
    with tempfile.TemporaryDirectory(prefix='recordmarks-tests-') as tmp:
        source = (f'TEST_ROOT={json.dumps(str(ROOT), ensure_ascii=False)}; '
                  f'TEST_OUTPUT={json.dumps(tmp)}; '
                  f'dofile(TEST_ROOT .. "/tests/obs_mock_tests.lua")')
        result = lua.luaL_loadstring(state, source.encode())
        if not result:
            result = lua.lua_pcallk(state, 0, 0, 0, 0, None)
        if result:
            raise AssertionError(lua.lua_tolstring(state, -1, None).decode('utf-8'))
        snapshots = list(pathlib.Path(tmp).glob('*.json'))
        for path in snapshots:
            doc = json.loads(path.read_text(encoding='utf-8'))
            assert doc['schema_version'] == 1
            assert isinstance(doc['markers'], list)
            ids = [mark['id'] for mark in doc['markers']]
            assert len(ids) == len(set(ids)), 'duplicate IDs'
            for mark in doc['markers']:
                assert mark['session_time_ms'] >= 0
                assert mark['session_frame'] >= 0
                if 'time_ms' in mark:
                    assert 0 <= mark['review_start_ms'] <= mark['time_ms'] <= mark['review_end_ms']
            if doc['format'] == 'obs-record-marks-session':
                assert isinstance(doc['files'], list)
                assert all(mark['file_index'] in [f['index'] for f in doc['files']] for mark in doc['markers'])
            else:
                rec = doc['recording']
                if not rec['file_offset_known']:
                    assert all('time_ms' not in mark for mark in doc['markers'])
                if 'duration_ms' in rec:
                    assert all(mark['review_end_ms'] <= rec['duration_ms'] for mark in doc['markers'])
        print(f'PASS: parsed and checked {len(snapshots)} UTF-8 JSON snapshots')
finally:
    lua.lua_close(state)

# Syntax and normalization checks for the offline viewer.
html = (ROOT / 'marker-viewer.html').read_text(encoding='utf-8')
code = html.split('<script>', 1)[1].split('</script>', 1)[0]
with tempfile.TemporaryDirectory(prefix='recordmarks-viewer-') as tmp:
    script = pathlib.Path(tmp) / 'syntax.js'
    script.write_text('new Function(' + json.dumps(code) + ');', encoding='utf-8')
    subprocess.run(['node', str(script)], check=True)
print('PASS: viewer JavaScript syntax')
subprocess.run(['node', str(ROOT / 'tests' / 'viewer_tests.js')], check=True)
