// Run with the existing Playwright Node tooling and an already running Blazor frontend.
// NODE_PATH may point to your existing global npm modules; no application dependency is added.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const origin = process.argv[2] || 'http://localhost:5173';
const snapshotId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const table = (id, name, columns, relationships = []) => ({ id, logicalName:name, physicalName:name, evidenceState:'Confirmed', columns, relationships, foreignKeys:relationships, evidence:[], indexes:[], constraints:[] });
const relationship = {id:'stock-item',fromTable:'stock',toTable:'item',fromColumns:['ItemId'],toColumns:['Id'],cardinality:'N:1',evidenceState:'Confirmed',evidence:[]};
const model = {sourceSnapshotId:snapshotId,sourceFingerprint:'generic-smoke-fixture',status:'Complete',databases:[{id:'inventory',logicalName:'Inventory candidate',schemas:[{name:'public',tables:[table('item','Item',[{name:'Id',isPrimaryKey:true},{name:'Name'}]),table('stock','Stock',[{name:'Id',isPrimaryKey:true},{name:'ItemId',isForeignKey:true}],[relationship])]}]}],unresolvedEvidence:[],conflicts:[],diagnostics:[],technologiesDetected:['EF Core']};
const source = {id:snapshotId,integrationId:'generic-source',archive:{fileName:'generic.zip',sha256:model.sourceFingerprint,fileCount:2},analyzedAt:'2026-01-01T00:00:00Z',status:'Ready',databaseArchitecture:model};
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  const page=await browser.newPage({viewport:{width:1440,height:1100}}), errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  await page.addInitScript(()=>localStorage.setItem('birknext:frontend-analysis-settings',JSON.stringify({activeProfileId:'generic-smoke',profiles:[{id:'generic-smoke',name:'Generic smoke',environmentType:'Development',targetUrl:'https://example.com'}]})));
  await page.route('**/api/source-analysis?*',route=>route.fulfill({status:200,contentType:'application/json',body:JSON.stringify([source]),headers:{'access-control-allow-origin':'*'}}));
  await page.goto(origin+'/source-analysis'); const workspace=page.getByTestId('database-workspace');await workspace.waitFor({timeout:60000});
  await workspace.getByRole('button',{name:'Diagram',exact:true}).click();const canvas=workspace.locator('.db-canvas');await workspace.locator('svg [data-table-id]').first().waitFor();assert.equal(await workspace.locator('[data-table-id]').count(),2);assert.equal(await workspace.locator('svg line').count(),1);
  await canvas.scrollIntoViewIfNeeded();const bounds=await canvas.boundingBox();const transform=()=>workspace.locator('svg > g').getAttribute('transform');let before=await transform();await page.mouse.move(bounds.x+bounds.width/2,bounds.y+bounds.height/2);await page.mouse.wheel(0,-200);await page.waitForTimeout(200);assert.notEqual(await transform(),before);
  const node=workspace.locator('[data-table-id="stock"]');await node.press('Enter');await workspace.locator('aside').waitFor();await workspace.getByRole('button',{name:'Focus selected table',exact:true}).click();const rect=await node.boundingBox();before=await node.getAttribute('transform');await page.mouse.move(rect.x+30,rect.y+20);await page.mouse.down();await page.mouse.move(rect.x+70,rect.y+40,{steps:4});await page.mouse.up();assert.notEqual(await node.getAttribute('transform'),before);
  await canvas.scrollIntoViewIfNeeded();const pan=await canvas.boundingBox();before=await transform();await page.mouse.move(pan.x+5,pan.y+5);await page.mouse.down();await page.mouse.move(pan.x+50,pan.y+30,{steps:4});await page.mouse.up();assert.notEqual(await transform(),before);
  await workspace.getByRole('button',{name:'Save layout',exact:true}).click();assert(await page.evaluate(()=>Object.keys(localStorage).some(k=>k.startsWith('database-layout:'))));
  const savedPosition=await node.getAttribute('transform');await page.reload();await workspace.waitFor();await workspace.getByRole('button',{name:'Diagram',exact:true}).click();await workspace.locator('[data-table-id="stock"]').waitFor();assert.equal(await workspace.locator('[data-table-id="stock"]').getAttribute('transform'),savedPosition);
  await workspace.getByRole('button',{name:'Fit to screen',exact:true}).click();await workspace.locator('[data-table-id="item"]').click();assert.equal(await workspace.locator('aside h3').textContent(),'Item');await workspace.locator('[data-table-id="stock"]').press('Enter');
  await workspace.getByRole('searchbox').fill('ItemId');await page.waitForTimeout(100);assert.equal(await workspace.locator('[data-table-id]').count(),1);await workspace.getByRole('searchbox').fill('');
  await workspace.getByRole('checkbox',{name:'PK/FK only',exact:true}).check();await workspace.getByRole('checkbox',{name:'PK/FK only',exact:true}).uncheck();await workspace.getByRole('button',{name:'Show related',exact:true}).click();await workspace.getByRole('button',{name:'Clear related focus',exact:true}).click();
  const widths=[];for(const width of [1440,1100,768,390]){await page.setViewportSize({width,height:1100});await page.waitForTimeout(100);const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth);assert.equal(overflow,false);widths.push(width);}
  const performance=[];for(const size of [10,50,100,200]){const result=await page.evaluate(async size=>{const diagram=await import('./js/databaseDiagram.js');const host=document.createElement('div');host.style.cssText='width:1000px;height:650px';document.body.append(host);const nodes=Array.from({length:size},(_,i)=>({id:'n'+i,name:'Table'+i,state:'Confirmed',columns:['PK Id','FK ParentId','Name']}));const start=window.performance.now();diagram.render(host,nodes,[],`generic-${size}`,{invokeMethodAsync:()=>Promise.resolve()});const milliseconds=window.performance.now()-start;const count=host.querySelectorAll('[data-table-id]').length;diagram.dispose(host);host.remove();return {size,milliseconds,count};},size);assert.equal(result.count,size);performance.push(result);}
  await workspace.getByRole('button',{name:'Tables',exact:true}).click();assert.equal(await workspace.locator('tbody tr').count(),2);await workspace.getByRole('button',{name:'Relationships',exact:true}).click();assert.equal(await workspace.locator('tbody tr').count(),1);assert.deepEqual(errors,[]);
  const result={passed:true,widths,performance,errors};if(process.argv[3])fs.writeFileSync(process.argv[3],JSON.stringify(result,null,2));console.log(JSON.stringify(result));
 } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
