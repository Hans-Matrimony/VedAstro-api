import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const assembly=fileURLToPath(new URL('./bin/Release/net10.0/VedAstro.LocalApi.dll',import.meta.url));
const dotnet=process.env.DOTNET_BIN||'dotnet';
const time={StdTime:'08:30 15/06/1990 +05:30',Location:{Name:'Synthetic Bengaluru',Latitude:12.9716,Longitude:77.5946}};
const checkTime={...time,StdTime:'12:00 17/10/2026 +00:00'};

test('Raman startup requires the explicit development profile gate',async()=>{
  for(const [profile,environment] of [['raman360','production'],['raman360',''],['raman365_25','development']]){
    const child=spawn(dotnet,[assembly],{env:{...process.env,VEDASTRO_SERVICE_TOKEN:crypto.randomBytes(32).toString('hex'),VEDASTRO_NATIVE_PROFILE:profile,VEDASTRO_NATIVE_PROFILE_ENVIRONMENT:environment},stdio:'ignore',windowsHide:true});
    try{
      const code=await Promise.race([new Promise(resolve=>child.once('exit',resolve)),new Promise((_,reject)=>setTimeout(()=>reject(Error('Invalid profile did not fail startup')),10000).unref())]);
      assert.notEqual(code,0);
    }finally{child.kill();}
  }
});

test('development Raman service keeps coordinates, periods, caches and settings consistent',async()=>{
  const key=crypto.randomBytes(32).toString('hex'),base='http://127.0.0.1:18963';
  const child=spawn(dotnet,[assembly],{env:{...process.env,VEDASTRO_SERVICE_TOKEN:key,ASPNETCORE_URLS:base,VEDASTRO_NATIVE_PROFILE:'raman360',VEDASTRO_NATIVE_PROFILE_ENVIRONMENT:'development'},stdio:['ignore','pipe','pipe'],windowsHide:true});
  child.stdout.resume();child.stderr.resume();
  async function call(operation,body){
    const r=await fetch(base+'/api/Calculate/'+operation,{method:'POST',headers:{'Content-Type':'application/json','x-api-key':key},body:JSON.stringify(body),signal:AbortSignal.timeout(20000)});
    return {status:r.status,cache:r.headers.get('x-calculation-cache'),data:await r.json()};
  }
  try{
    let ready;
    for(let i=0;i<60;i++){
      assert.equal(child.exitCode,null);
      try{const r=await fetch(base+'/ready',{signal:AbortSignal.timeout(1000)});if(r.ok){ready=await r.json();break;}}catch{}
      await new Promise(resolve=>setTimeout(resolve,200));
    }
    assert.ok(ready);assert.equal(ready.calculation_profile,'raman360');assert.equal(ready.ayanamsa,'RAMAN');assert.equal(ready.dasha_year_days,360);
    const natal=await call('NatalEvidence',{time,Ayanamsa:'RAMAN'});
    assert.equal(natal.status,200);
    const settings=natal.data.CalculationSettings;
    assert.equal(settings.ayanamsa,'RAMAN');assert.equal(settings.dasha_year_days,360);
    assert.notEqual(natal.data.Input.Ayanamsa,1);
    const period=await call('DasaAtTime',{birthTime:time,checkTime,levels:2,Ayanamsa:'RAMAN'});
    assert.equal(period.status,200);assert.deepEqual(period.data.Payload.DasaAtTime,{PD1:'Saturn',PD2:'Jupiter'});
    const daily=await call('DailyEvidence',{time,checkTime,Ayanamsa:'RAMAN'});
    assert.equal(daily.status,200);assert.deepEqual(daily.data.CalculationSettings,settings);
    assert.deepEqual(daily.data.Payload.DailyEvidence.period,period.data.Payload.DasaAtTime);
    const reading=await call('ReadingEvidence',{time,checkTime,topic:'marriage',Ayanamsa:'RAMAN'});
    assert.equal(reading.status,200);assert.deepEqual(reading.data.CalculationSettings,settings);
    assert.deepEqual(reading.data.Payload.ReadingEvidence.period,period.data.Payload.DasaAtTime);
    assert.equal((await call('NatalEvidence',{time,Ayanamsa:'LAHIRI'})).status,400);
    const repeat=await call('NatalEvidence',{time,Ayanamsa:'RAMAN'});
    assert.equal(repeat.cache,'hit');assert.deepEqual(repeat.data,natal.data);
    assert.equal((await (await fetch(base+'/ready')).json()).dasha_year_days,360);
  }finally{child.kill();}
});
