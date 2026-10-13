// Regression checks for the package example's list form, without launching a browser.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
class Element {
  constructor(tag = 'div') {
    this.tag = tag;
    this.children = []; this.options = []; this.value = ''; this.dataset = {};
    this.disabled = false; this.hidden = false; this.textContent = '';
  }
  append(...nodes) {
    this.children.push(...nodes);
    if (this.tag === 'select') this.options.push(...nodes);
  }
  replaceChildren(...nodes) {
    this.children = nodes;
    if (this.tag === 'select') this.options = nodes;
  }
  addEventListener(type, handler) { (this.listeners ||= {})[type] = handler; }
  dispatch(type) { this.listeners?.[type]?.({ currentTarget: this, preventDefault() {} }); }
  setAttribute() {} scrollIntoView() {} focus() {}
  querySelector(selector) { return (this.parts ||= {})[selector] ||= new Element(); }
  querySelectorAll(selector) {
    return this.children.flatMap(child => [
      ...(child.tag === 'select' ? [child] : []), ...child.querySelectorAll(selector)
    ]);
  }
}
const elements = new Map();
const get = id => {
  if (!elements.has(id)) elements.set(id, new Element());
  return elements.get(id);
};
get('lists-app').dataset.canEdit = 'true';
get('workspace').options = [new Element('option')];
const document = {
  getElementById: get, createElement: tag => new Element(tag), querySelector: () => ({ content: 'csrf' })
};
const calls = [];
let responses = [];
const context = {
  document, structuredClone, crypto: require('node:crypto').webcrypto, console, BigInt, Number,
  fetch: async (path, options) => {
    calls.push({ path, ...options });
    const result = await responses.shift();
    if (result instanceof Error) throw result;
    return { ok: result.status < 400, status: result.status, text: async () => JSON.stringify(result.body) };
  }
};
vm.createContext(context);
const sourcePath = path.resolve(__dirname, '../../examples/DocumentLibrary/wwwroot/lists.js');
let source = fs.readFileSync(sourcePath, 'utf8');
// Expose the closure's operations inside this isolated test VM only.
source = source.replace(/\}\)\(\);\s*$/, `globalThis.test = {
  state, saveRecord, saveMerge, parseJson, bodyJson, valueText, captureContext,
  run, closeDraft, loadList, loadWorkspace, loadRecords, openRecord, references, addField, createList
};})();`);
vm.runInContext(source, context);
const test=context.test;
const exactNumbers = test.parseJson('{"revision":9223372036854775807,"bigInteger":-9223372036854775808,"decimal":1234567890123456.123456789012,"exponent":1.234567890123456789012345678e+5,"zero":-0,"id":"9223372036854775807"}');
assert.equal(test.valueText(exactNumbers.revision),'9223372036854775807');
assert.equal(test.valueText(exactNumbers.decimal),'1234567890123456.123456789012');
const exactRoundTrip = test.bodyJson(structuredClone(exactNumbers));
assert.equal(exactRoundTrip,'{"revision":9223372036854775807,"bigInteger":-9223372036854775808,"decimal":1234567890123456.123456789012,"exponent":1.234567890123456789012345678e+5,"zero":-0,"id":"9223372036854775807"}');
const markerText = '{"__cellBridgeListRawNumber":"1"}';
assert.equal(JSON.parse(test.bodyJson({ text: markerText })).text, markerText);
assert.throws(() => test.bodyJson({ __cellBridgeListRawNumber: '1,"injected":true' }), /Invalid internal number token/);
assert.throws(() => test.bodyJson({ __cellBridgeListRawNumber: '1', extra: true }), /Invalid internal number token/);
const fieldId='73000000-0000-0000-0000-000000000001', otherId='73000000-0000-0000-0000-000000000002';
const definition={id:'list',schemaVersion:3,name:'Tasks',fields:[{id:fieldId,name:'Title',type:'Text'},{id:otherId,name:'Status',type:'Text'}]};
get('workspace').value='workspace';get('list').value='list';
get('workspace').tag='select';get('list').tag='select';
const originalContext = test.captureContext(); test.state.context = originalContext;
const control=new Element('input');control.value='mine';
test.state.draft={id:'record',context:originalContext,original:structuredClone({[fieldId]:'original',[otherId]:'untouched',integer:exactNumbers.bigInteger,decimal:exactNumbers.decimal}),revision:exactNumbers.revision,controls:new Map([[fieldId,{field:definition.fields[0],control,initial:'original'}]])};
responses=[{status:200,body:definition},{status:409},{status:200,body:definition},{status:200,body:{currentRevision:2,schemaVersion:3,suggestedChanges:[],fields:[{fieldId,originalPresent:true,originalValue:'original',currentPresent:true,currentValue:'theirs',proposedPresent:true,proposedValue:'mine',conflict:true},{fieldId:otherId,originalPresent:true,originalValue:'untouched',currentPresent:true,currentValue:'updated',proposedPresent:true,proposedValue:'untouched',conflict:false}]}}];
(async () => {
  await test.saveRecord();
  assert.equal(test.state.draft.original[fieldId],'original');assert.equal(control.value,'mine');
  assert.ok(calls[1].body.includes('\"expectedRevision\":9223372036854775807'));
  assert.ok(calls[3].body.includes('\"integer\":-9223372036854775808'));
  assert.ok(calls[3].body.includes('\"decimal\":1234567890123456.123456789012'));
  assert.ok(calls[3].body.includes('\"value\":\"mine\"'));
  assert.equal(JSON.parse(calls[1].body).schemaVersion,3);assert.equal(JSON.parse(calls[3].body).schemaVersion,3);
  assert.equal(get('merge-fields').children.length,2);assert.equal(get('merge-fields').children[1].children[4].textContent,'Keep their change');
  const resolutions=get('merge-fields').querySelectorAll('select');resolutions[0].value='mine';
  const preview=test.state.preview;
  responses=[{status:200,body:definition},{status:409},{status:200,body:definition},{status:200,body:{...preview,currentRevision:4}}];
  await test.saveMerge();assert.equal(test.state.preview.currentRevision,4);assert.equal(test.state.draft.original[fieldId],'original');assert.equal(control.value,'mine');
  // An unconfirmed create freezes its ID and exact payload for the next retry.
  test.state.preview=null;const createControl=new Element('input');createControl.value='new title';
  test.state.draft={id:null,context:originalContext,createId:'fixed-create-id',definition,original:{},controls:new Map([[fieldId,{field:definition.fields[0],control:createControl,initial:''}]]),pendingCreate:null};
  responses=[{status:200,body:definition},new Error('lost response')];
  await assert.rejects(test.saveRecord());const pending=JSON.stringify(test.state.draft.pendingCreate);
  assert.equal(createControl.disabled,true);
  responses=[new Error('another lost response')];await assert.rejects(test.saveRecord());
  assert.equal(JSON.stringify(test.state.draft.pendingCreate),pending);
  assert.equal(calls.at(-1).body,calls.at(-2).body);
  // A blocked definition read cannot redirect a draft save to a new customer.
  test.state.preview=null;
  const originalDraft={id:'record',context:originalContext,original:{[fieldId]:'original'},revision:1,
   controls:new Map([[fieldId,{field:definition.fields[0],control,initial:'original'}]])};
  test.state.draft=originalDraft;test.state.context=originalContext;
  get('workspace').value='workspace';get('list').value='list';get('list').options=[new Element('option')];
  let unblock;const blocked=new Promise(resolve=>{unblock=resolve});
  responses=[blocked,{status:200,body:{}},{status:200,body:{records:[],nextCursor:null}}];
  const offset=calls.length;
  const saving=test.run(test.saveRecord,get('save-record'));
  assert.equal(get('workspace').disabled,true);assert.equal(get('list').disabled,true);
  assert.equal(get('cancel-record').disabled,true);assert.equal(get('back-to-draft').disabled,true);
  get('cancel-record').dispatch('click');get('back-to-draft').dispatch('click');
  assert.equal(test.state.draft,originalDraft);
  // Synthetic value changes bypass HTML's disabled UI, but cannot alter captured paths.
  get('workspace').value='other-workspace';get('list').value='other-list';
  get('workspace').dispatch('change');get('list').dispatch('change');
  assert.equal(calls.length,offset+1);
  unblock({status:200,body:definition});await saving;
  assert.equal(calls[offset+1].method,'PATCH');
  assert.equal(calls[offset+1].path,originalContext.listRoot+'/records/record');
  assert.equal(calls[offset+2].path,originalContext.listRoot+'/records/query');
  assert.equal(get('cancel-record').disabled,false);
  // closeDraft within loadList keeps navigation locked through the pending read.
  test.state.draft=null;get('workspace').value='workspace';get('list').value='list';
  let unblockLoad;const blockedLoad=new Promise(resolve=>{unblockLoad=resolve});
  responses=[blockedLoad,{status:200,body:{records:[],nextCursor:null}}];
  const loading=test.run(()=>test.loadList(originalContext));
  assert.equal(get('workspace').disabled,true);assert.equal(get('list').disabled,true);
  unblockLoad({status:200,body:definition});await loading;
  // Creates also retain their draft workspace while fetching the current schema.
  const createDraft={id:null,context:originalContext,createId:'immutable-create',definition,original:{},
   controls:new Map([[fieldId,{field:definition.fields[0],control:new Element('input'),initial:''}]]),pendingCreate:null};
  createDraft.controls.get(fieldId).control.value='new record';test.state.draft=createDraft;
  let unblockCreate;const blockedCreate=new Promise(resolve=>{unblockCreate=resolve});
  responses=[blockedCreate,new Error('response lost')];const createOffset=calls.length;
  const creating=test.run(test.saveRecord);
  get('cancel-record').dispatch('click');get('workspace').value='other-workspace';get('list').value='other-list';
  unblockCreate({status:200,body:definition});await creating;
  assert.equal(calls[createOffset+1].path,originalContext.listRoot+'/records');
  assert.equal(test.state.draft,createDraft);
  // Schema publication snapshots both the route and the field name before its GET.
  test.state.draft=null;test.state.context=originalContext;
  const form={elements:{name:{value:'Notes'}},reset(){}};
  let unblockSchema;const blockedSchema=new Promise(resolve=>{unblockSchema=resolve});
  responses=[blockedSchema,{status:200,body:definition}];const schemaOffset=calls.length;
  const designing=test.run(()=>test.addField(form));
  get('workspace').value='different-customer';form.elements.name.value='Changed during request';
  unblockSchema({status:200,body:definition});await designing;
  assert.equal(calls[schemaOffset+1].path,originalContext.listRoot+'/schema');
  assert.equal(JSON.parse(calls[schemaOffset+1].body).fields.at(-1).name,'Notes');
  // A reference target uses the captured draft workspace for every page.
  let unblockReference;const blockedReference=new Promise(resolve=>{unblockReference=resolve});
  responses=[blockedReference,{status:200,body:{records:[],nextCursor:null}}];const referenceOffset=calls.length;
  const resolving=test.run(()=>test.references({referenceListId:'target-list'},originalContext));
  get('workspace').value='another-workspace';unblockReference({status:200,body:definition});await resolving;
  assert.equal(calls[referenceOffset].path,originalContext.workspaceRoot+'/target-list');
  assert.equal(calls[referenceOffset+1].path,originalContext.workspaceRoot+'/target-list/records/query');
  // Merge resolution retains the same draft route across its schema GET.
  test.state.draft=originalDraft;test.state.preview={currentRevision:2,schemaVersion:3,suggestedChanges:[{fieldId,value:'mine'}],fields:[]};
  get('merge-fields').replaceChildren();
  let unblockMerge;const blockedMerge=new Promise(resolve=>{unblockMerge=resolve});
  responses=[blockedMerge,{status:200,body:{}},{status:200,body:{records:[],nextCursor:null}}];const mergeOffset=calls.length;
  const merging=test.run(test.saveMerge);
  get('back-to-draft').dispatch('click');assert.ok(test.state.preview);
  get('workspace').value='different-customer';get('list').value='different-list';
  unblockMerge({status:200,body:definition});await merging;
  assert.equal(calls[mergeOffset+1].path,originalContext.listRoot+'/records/record');
  // A custom-list retry and existence check stay in the captured customer workspace.
  test.state.draft=null;test.state.pendingList=null;get('workspace').value='workspace';get('list').value='list';
  let unblockList;const blockedList=new Promise(resolve=>{unblockList=resolve});
  const listForm={elements:{name:{value:'J'}},reset(){}};
  responses=[blockedList,{status:404}];const listOffset=calls.length;
  const createListButton=get('create-list').querySelector('button');
  const listCreating=test.run(()=>test.createList(listForm),createListButton);
  get('workspace').value='another-workspace';unblockList(new Error('uncertain create'));await listCreating;
  assert.equal(calls[listOffset].path,originalContext.workspaceRoot);
  assert.equal(calls[listOffset+1].path,originalContext.workspaceRoot+'/'+test.state.pendingList.id);
  assert.equal(get('workspace').disabled,true);
  assert.equal(get('list').disabled,true);assert.equal(get('new-record').disabled,true);
  assert.equal(get('add-field').querySelector('button').disabled,true);
  assert.equal(createListButton.disabled,false);
  responses=[new Error('still uncertain'),{status:404}];const retryOffset=calls.length;
  await test.run(()=>test.createList(listForm),createListButton);
  assert.equal(createListButton.disabled,false);assert.equal(get('workspace').disabled,true);
  assert.equal(calls[retryOffset].path,originalContext.workspaceRoot);
  assert.equal(calls[retryOffset].body,calls[listOffset].body);
  // A failed or empty customer discovery must never reuse the previous customer.
  test.state.pendingList=null;test.state.pendingListContext=null;
  async function assertNavigationUnavailable(action, queued) {
   test.state.context=originalContext;test.state.definition=definition;
   test.state.records=[{id:'old-record'}];test.state.cursor='old-cursor';
   get('workspace').value='other-workspace';get('list').value='list';
   responses=queued;const before=calls.length;
   await test.run(action);
   assert.equal(test.state.context,null);assert.equal(test.state.definition,null);
   assert.equal(test.state.records.length,0);assert.equal(test.state.cursor,null);
   assert.equal(get('list-content').hidden,true);
   assert.equal(get('new-record').disabled,true);assert.equal(get('refresh').disabled,true);
   assert.equal(get('add-field').querySelector('button').disabled,true);
   assert.equal(get('create-list').querySelector('button').disabled,true);
   assert.equal(calls[before].path.startsWith('/api/workspaces/other-workspace/lists'),true);
   const after=calls.length;
   await test.run(()=>test.openRecord());await test.run(()=>test.loadRecords());
   get('new-record').dispatch('click');get('refresh').dispatch('click');
   await Promise.resolve();
   assert.equal(calls.length,after);
   assert.equal(test.state.draft,null);
  }
  await assertNavigationUnavailable(()=>test.loadWorkspace(),[{status:503}]);
  await assertNavigationUnavailable(()=>test.loadWorkspace(),[{status:200,body:[]}]);
  await assertNavigationUnavailable(()=>test.loadList(),[{status:404}]);
  console.log('List UI checks passed: exact numeric snapshots, draft/retry retention, immutable async targets, and failed/empty navigation guards.');
})().catch(error => { console.error(error); process.exitCode = 1; });
