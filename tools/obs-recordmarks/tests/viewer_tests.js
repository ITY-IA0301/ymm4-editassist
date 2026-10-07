// Runs the shipped browser code in a minimal DOM stub. Not a browser UI test.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '..', 'marker-viewer.html'), 'utf8');
const source = html.split('<script>')[1].split('</script>')[0];
class Element {
  constructor(){this.value='';this.listeners={};this.children=[];this.attrs={};this.duration=100;this.readyState=1;this.currentTime=0;}
  addEventListener(name,fn){this.listeners[name]=fn;}
  replaceChildren(){this.children=[];}
  append(x){this.children.push(x);}
  getAttribute(key){return this.attrs[key]||null;}
  removeAttribute(key){delete this.attrs[key];}
  set src(value){this.attrs.src=value;}
  pause(){}
  load(){}
  play(){return Promise.resolve();}
  click(){this.listeners.click?.();}
}
const elements=new Map();
const context=vm.createContext({console,Map,Set,Number,String,Array,Math,Infinity,JSON,Blob,URL,setTimeout,
  document:{getElementById:id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id);},
    createElement:()=>new Element(),createTextNode:text=>({textContent:text})},
  window:{addEventListener(){}}});
vm.runInContext(source,context);
let passed=0;
function check(name,fn){fn();passed++;console.log('PASS '+name);}
const evaluate=s=>vm.runInContext(s,context);
const sidecar={format:'obs-record-marks',schema_version:1,session_id:'session-A',updated_at_utc:'2026-10-07T00:00:00Z',
  recording:{filename:'record.mkv',path:'C:/録画/record.mkv',index:1,duration_ms:10000},
  timing:{quality:'output_frame_estimate'},markers:[{id:1,kind:'highlight',label:'見せ場',file_index:1,time_ms:5000,
    session_time_ms:5000,review_start_ms:0,review_end_ms:10000,note:'<img src=x onerror=alert(1)>'}]};
context.fixture=sidecar;
check('per-file normalization retains timestamp',()=>assert.equal(evaluate('normalize(fixture)[0].time_ms'),5000));
check('missing file timestamp remains unknown',()=>{
  context.unknown=JSON.parse(JSON.stringify(sidecar));delete context.unknown.markers[0].time_ms;
  assert.equal(evaluate('normalize(unknown)[0].time_ms'),null);
});
context.manifest={format:'obs-record-marks-session',schema_version:1,session_id:'session-A',updated_at_utc:'2026-10-07T00:00:10Z',
  files:[{index:1,filename:'record.mkv',path:'C:/録画/record.mkv',start_session_ms:60000,end_session_ms:80000,file_offset_known:true}],
  markers:[{id:2,kind:'completion',file_index:1,session_time_ms:65000,before_ms:30000,after_ms:15000}]};
check('manifest derives split-relative time',()=>assert.equal(evaluate('normalize(manifest)[0].time_ms'),5000));
check('manifest flags cross-file context',()=>assert.equal(evaluate('normalize(manifest)[0].context_crosses_file'),true));
check('invalid format rejected',()=>assert.throws(()=>evaluate('normalize({schema_version:999,markers:[]})')));
check('malformed manifest filename rejected',()=>{
  context.invalid=JSON.parse(JSON.stringify(context.manifest));context.invalid.files[0].filename=42;
  assert.throws(()=>evaluate('normalize(invalid)'));
});
check('CSV formula prefix escaped',()=>assert.equal(evaluate('cell("=HYPERLINK(1)")'),'"\'=HYPERLINK(1)"'));
check('CSV quotes and newline retained',()=>assert.equal(evaluate('cell("a\\n\\\"b")'),'"a\n""b"'));
evaluate('marks=normalize(fixture); render();');
check('notes inserted as text nodes',()=>{
  const mark=elements.get('rows').children[0], note=mark.children.find(e=>e.className==='note');
  assert.equal(note.textContent,sidecar.markers[0].note);assert.equal(note.innerHTML,undefined);
});
elements.get('search').value='record';evaluate('render()');
check('search filters records',()=>assert.equal(elements.get('rows').children.length,1));
elements.get('kind').value='completion';evaluate('render()');
check('kind filter hides nonmatching markers',()=>assert.equal(elements.get('rows').children.length,0));
elements.get('kind').value='';elements.get('search').value='';
evaluate('videos=new Map([["record.mp4",{url:"blob:remux"}]]);');
check('unique remux filename is matched',()=>assert.equal(evaluate('lookupVideo("record.mkv").url'),'blob:remux'));
evaluate('videos.set("record.mov",{url:"blob:other"});');
check('ambiguous remux is rejected',()=>assert.equal(evaluate('lookupVideo("record.mkv").ambiguous'),true));
check('unknown marker clears previously loaded video',()=>{
  elements.get('player').src='blob:previous';evaluate('select(normalize(unknown)[0],true)');
  assert.equal(elements.get('player').getAttribute('src'),null);
});
async function main(){
  const file=d=>({name:'marks.json',text:async()=>JSON.stringify(d)});
  await elements.get('jsonFiles').listeners.change({target:{files:[file(sidecar),file(context.manifest)]}});
  check('manifest wins over stale sidecar after undo',()=>assert.equal(evaluate('marks.length'),1));
  check('stale marker is not resurrected',()=>assert.equal(evaluate('marks[0].id'),2));
  const newer={...context.manifest,updated_at_utc:'2026-10-07T00:00:20Z',markers:[]};
  await elements.get('jsonFiles').listeners.change({target:{files:[file(context.manifest),file(newer)]}});
  check('newest complete snapshot can remove all markers',()=>assert.equal(evaluate('marks.length'),0));
  console.log('Viewer assertions: '+passed);
}
main().catch(e=>{console.error(e);process.exitCode=1;});
