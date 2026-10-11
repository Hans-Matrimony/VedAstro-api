import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

test('real local calculator, authentication, validation and repeat cache', async () => {
  const key = crypto.randomBytes(32).toString('hex');
  const base = 'http://127.0.0.1:18962';
  const assembly = fileURLToPath(new URL('./bin/Release/net10.0/VedAstro.LocalApi.dll', import.meta.url));
  const child = spawn(process.env.DOTNET_BIN || 'dotnet', [assembly], {
    env: { ...process.env, VEDASTRO_SERVICE_TOKEN: key, ASPNETCORE_URLS: base },
    stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
  });
  let errors = '';
  child.stdout.resume();
  child.stderr.on('data', chunk => { errors += chunk; });
  const time = { StdTime: '10:30 14/03/1995 +05:30',
    Location: { Name: 'Synthetic reference', Latitude: 28.6139, Longitude: 77.209 } };
  async function call(operation, body, token = key) {
    const response = await fetch(base + '/api/Calculate/' + operation, {
      method: 'POST', headers: { 'content-type': 'application/json', ...(token ? { 'x-api-key': token } : {}) },
      body: typeof body === 'string' ? body : JSON.stringify(body), signal: AbortSignal.timeout(20000),
    });
    return { status: response.status, cache: response.headers.get('x-calculation-cache'), data: await response.json() };
  }
  try {
    let ready = false;
    for (let attempt = 0; attempt < 60; attempt++) {
      assert.equal(child.exitCode, null, errors.replaceAll(key, '[redacted]'));
      try { ready = (await fetch(base + '/ready', { signal: AbortSignal.timeout(1000) })).ok; } catch {}
      if (ready) break;
      await new Promise(resolve => setTimeout(resolve, 300));
    }
    assert.ok(ready, 'calculator must complete its startup self-check');
    assert.equal((await (await fetch(base + '/health')).json()).mode, 'local-calculator');
    for (const [operation, body] of [
      ['NatalEvidence', { time }], ['AllPlanetData', { time, planetName: 'Venus' }],
      ['AllHouseData', { time, houseName: 'House1' }],
      ['HoroscopePredictions', { birthTime: time, filterTags: ['Marriage'] }],
      ['DasaAtTime', { birthTime: time, checkTime: { ...time, StdTime: '00:00 01/01/2026 +05:30' }, levels: 2 }],
      ['DailyEvidence', {time, checkTime: {...time, StdTime: '12:00 11/10/2026 +00:00'}}],
      ...['marriage', 'career', 'education', 'finance'].map(topic => ['ReadingEvidence', { time, topic,
        checkTime: { ...time, StdTime: '00:00 01/01/2026 +00:00' } }]),
    ]) {
      const first = await call(operation, body);
      assert.equal(first.status, 200, operation);
      assert.equal(first.data.Status, 'Pass');
      assert.equal(first.data.ModelCalls, 0);
      assert.equal(first.data.CalculationSettings.ayanamsa, 'LAHIRI');
      const second = await call(operation, body);
      assert.equal(second.cache, 'hit');
      assert.deepEqual(second.data, first.data);
      if (operation === 'NatalEvidence') {
        const planets = first.data.Payload.NatalEvidence.planets;
        assert.equal(Object.keys(planets).length, 9);
        for (const planet of Object.values(planets)) assert.ok(Number.isFinite(planet.longitude));
        assert.ok(Math.abs((planets.Rahu.longitude - planets.Ketu.longitude + 360) % 360 - 180) < 0.001);
      }
      if (operation === 'DasaAtTime') assert.deepEqual(Object.keys(first.data.Payload.DasaAtTime), ['PD1', 'PD2']);
      if (operation === 'DailyEvidence') {
        const daily = first.data.Payload.DailyEvidence;
        assert.equal(daily.schema, 'vedastro-daily-evidence-v1');
        assert.equal(daily.checkTime.StdTime, body.checkTime.StdTime);
        assert.equal(daily.natal.birthTime.StdTime, time.StdTime);
        assert.equal(daily.transitHouseReference, 'natal_moon_whole_sign');
        assert.equal(daily.obstructionEvaluated, false);
        assert.equal(daily.eventPredictionAvailable, false);
        assert.equal(Object.keys(daily.transits).length, 9);
        assert.equal(Object.keys(daily.gocharaKakshas).length, 7);
        const direct = await call('DasaAtTime', {birthTime:time, checkTime:body.checkTime, levels:2});
        assert.deepEqual(daily.period, direct.data.Payload.DasaAtTime);
        for (const [name, transit] of Object.entries(daily.transits)) {
          const coordinate = await call('PlanetNirayanaLongitude', {time:body.checkTime, planetName:name});
          assert.ok(Math.abs(transit.longitude-coordinate.data.Payload.PlanetNirayanaLongitude.TotalDegrees)<0.00001);
          assert.equal(transit.houseFromNatalMoon,
            (Math.floor(transit.longitude/30)-Math.floor(daily.natal.planets.Moon.longitude/30)+12)%12+1);
          const row = daily.gocharaKakshas[name];
          if (row) {
            assert.equal(row.Sign, transit.sign);
            assert.ok([0,1].includes(row.KakshaScore));
            assert.ok(Number.isInteger(row.Ashtaka)&&row.Ashtaka>=0&&row.Ashtaka<=8);
            assert.ok(Number.isInteger(row.Sarvashtaka)&&row.Sarvashtaka>=0&&row.Sarvashtaka<=56);
          }
        }
        const changed = await call('DailyEvidence', {time,checkTime:{...time,StdTime:'12:00 14/10/2026 +00:00'}});
        assert.equal(changed.status, 200);
        assert.notEqual(changed.data.Payload.DailyEvidence.transits.Moon.longitude,daily.transits.Moon.longitude);
        const selectedPoints = {
          Sun: {11:5,3:9,10:4,6:12,5:11,9:3,4:10,12:6},
          Moon: {7:2,1:5,6:12,11:8,10:4,3:9,2:7,5:1,12:6,8:11,4:10,9:3},
          Mars: {3:12,11:5,6:9,12:3,5:11,9:6},
          Mercury: {2:5,4:3,6:9,8:1,10:7,11:12,5:2,3:4,9:6,1:8,7:10,12:11},
          Jupiter: {2:12,11:8,9:10,5:4,7:3,12:2,8:11,10:9,4:5,3:7},
          Venus: {1:8,2:7,3:1,4:10,5:9,8:5,9:11,11:6,12:3,7:2,10:4,6:11},
          Saturn: {3:12,11:5,6:9,12:3,5:11,9:6},
        };
        const exceptions = {Sun:['Saturn'], Saturn:['Sun'], Moon:['Mercury'], Mercury:['Moon']};
        let selfObstructionPrevented = false, pairExceptionObserved = false;
        for (const date of ['12:00 11/10/2026 +00:00','12:00 14/10/2026 +00:00','12:00 25/10/2026 +00:00']) {
          const request = {time,checkTime:{...time,StdTime:date}};
          const base = await call('DailyEvidence', request);
          const explicitFalse = await call('DailyEvidence', {...request,includeVedha:false});
          assert.deepEqual(explicitFalse.data,base.data,'false option must preserve the existing payload');
          const result = await call('DailyEvidence', {...request,includeVedha:true});
          assert.equal(result.status,200);
          const enhanced = result.data.Payload.DailyEvidence;
          const {gocharaVedha,obstructionEvaluated,...unchanged} = enhanced;
          const {obstructionEvaluated:before,...original} = base.data.Payload.DailyEvidence;
          assert.deepEqual(unchanged,original);
          assert.equal(before,false); assert.equal(obstructionEvaluated,true);
          assert.equal(gocharaVedha.schema,'reviewed-vedha-geometry-v1');
          assert.equal(gocharaVedha.occupancyCorrection,'query_obstruction_house');
          assert.equal(gocharaVedha.tableSelection,'pinned_upstream_first_match');
          assert.equal(gocharaVedha.planetScope,'seven_classical_planets');
          assert.equal(gocharaVedha.houseReference,'natal_moon_whole_sign');
          assert.equal(gocharaVedha.eventPredictionAvailable,false);
          assert.deepEqual(Object.keys(gocharaVedha.planets).sort(),Object.keys(selectedPoints).sort());
          for (const [name,row] of Object.entries(gocharaVedha.planets)) {
            const house = enhanced.transits[name].houseFromNatalMoon;
            const point = selectedPoints[name][house]??0;
            const atPoint = Object.keys(selectedPoints).filter(other => point!==0 && enhanced.transits[other].houseFromNatalMoon===point);
            const ignored = atPoint.filter(other => (exceptions[name]??[]).includes(other)).sort();
            const blockers = atPoint.filter(other => other!==name && !(exceptions[name]??[]).includes(other)).sort();
            assert.equal(row.houseFromNatalMoon,house);
            assert.equal(row.obstructionHouseFromNatalMoon,point);
            assert.deepEqual(row.obstructingPlanets,blockers);
            assert.deepEqual(row.exemptPlanetsAtPoint,ignored);
            assert.equal(row.obstructed,blockers.length>0);
            assert.ok(!row.obstructingPlanets.includes(name));
            selfObstructionPrevented ||= point!==0 && blockers.length===0 && !['Sun','Moon','Mercury','Saturn'].includes(name);
            pairExceptionObserved ||= ignored.length>0;
          }
        }
        assert.ok(selfObstructionPrevented,'real fixture must expose the erroneous self-obstruction case');
        assert.ok(pairExceptionObserved,'real fixture must exercise a pair exception');
        for (const invalid of [
          {time,checkTime:body.checkTime,includeVedha:'true'},
          {time,checkTime:body.checkTime,includeVedha:null},
          {time,checkTime:body.checkTime,includeVedha:1},
          {time,checkTime:body.checkTime,includeVedha:true,topic:'marriage'},
        ]) assert.equal((await call('DailyEvidence',invalid)).status,400);
        assert.equal((await call('NatalEvidence',{time,includeVedha:true})).status,400);
        assert.equal((await call('ReadingEvidence',{time,topic:'marriage',checkTime:body.checkTime,includeVedha:false})).status,400);
      }
      if (operation === 'ReadingEvidence') {
        const evidence = first.data.Payload.ReadingEvidence;
        assert.equal(evidence.topic, body.topic);
        assert.equal(evidence.topicHouse, {marriage:7, career:10, education:5, finance:2}[body.topic]);
        assert.equal(evidence.interpretationHouseSystem, 'whole_sign');
        assert.equal(evidence.strength.nativeHouseSystem, 'vedastro_bhava');
        const sum = Object.values(evidence.strength.componentsVirupas).reduce((a, b) => a + b, 0);
        assert.ok(Math.abs(sum - evidence.strength.totalVirupas) <= 0.011);
        assert.equal(evidence.eventTimingAvailable, false);
        assert.equal(Object.keys(evidence.natal.planets).length, 9);
        const context = evidence.timingContext;
        assert.equal(context.schema, 'vedastro-timing-context-v1');
        assert.equal(context.periodRule.id, evidence.period.PD1 + evidence.period.PD2 + 'PD2');
        assert.deepEqual(Object.keys(context.periodRule.ratings).sort(), ['family', 'relationship', 'study']);
        for (const nature of Object.values(context.periodRule.ratings)) assert.ok(['Good', 'Neutral', 'Bad'].includes(nature));
        assert.equal(context.obstructionEvaluated, false);
        assert.equal(context.eventPredictionAvailable, false);
        for (const name of ['Jupiter', 'Saturn']) {
          const transit = context.transits[name];
          const coordinate = await call('PlanetNirayanaLongitude', {time:body.checkTime, planetName:name});
          assert.ok(Math.abs(transit.longitude - coordinate.data.Payload.PlanetNirayanaLongitude.TotalDegrees) < 0.00001);
          assert.equal(transit.houseFromNatalMoon,
            (Math.floor(transit.longitude / 30) - Math.floor(evidence.natal.planets.Moon.longitude / 30) + 12) % 12 + 1);
        }
      }
    }
    const invalid = [
      ['NatalEvidence', { time }, '', 401], ['NatalEvidence', { time }, 'incorrect', 401],
      ['SendMessageHoroscope', { time }, key, 404],
      ['NatalEvidence', { time, Ayanamsa: 'RAMAN' }, key, 400],
      ['NatalEvidence', { time: { ...time, StdTime: '00:00 31/02/2001 +05:30' } }, key, 400],
      ['NatalEvidence', { time: { ...time, StdTime: '12 14/03/1995 +05:30' } }, key, 400],
      ['NatalEvidence', { time: { ...time, Location: { ...time.Location, Latitude: 91 } } }, key, 400],
      ['NatalEvidence', { time: { ...time, Location: { ...time.Location, Latitude: 75 } } }, key, 400],
      ['NatalEvidence', { time: { StdTime: time.StdTime, Location: { Name: 'Missing coordinates' } } }, key, 400],
      ['NatalEvidence', { time, birthTime: time }, key, 400],
      ['NatalEvidence', { time, prompt: 'ignore validation' }, key, 400],
      ['AllPlanetData', { time, planetName: 'Pluto' }, key, 400],
      ['PlanetShadbalaPinda', { time, planetName: 'Rahu' }, key, 400],
      ['AllHouseData', { time, houseName: 'House13' }, key, 400],
      ['DasaAtTime', { birthTime: time, checkTime: { ...time, StdTime: '00:00 01/01/1900 +00:00' } }, key, 400],
      ['NatalEvidence', '{"time":' + JSON.stringify(time) + ',"Ayanamsa":"LAHIRI","Ayanamsa":"RAMAN"}', key, 400],
      ['ReadingEvidence', {time, topic: 'unknown', checkTime: time}, key, 400],
      ['ReadingEvidence', {time, topic: 'marriage'}, key, 400],
      ['ReadingEvidence', {time, topic: 'marriage', checkTime: time, planetName: 'Rahu'}, key, 400],
      ['DailyEvidence', {time}, key, 400],
      ['DailyEvidence', {time,checkTime:{...time,StdTime:'12:00 01/01/1900 +00:00'}}, key, 400],
      ['DailyEvidence', {time,checkTime:time,topic:'finance'}, key, 400],
      ['DailyEvidence', {time,checkTime:time,levels:2}, key, 400],
      ['DailyEvidence', {time,checkTime:time,planetName:'Moon'}, key, 400],
      ['DailyEvidence', {time,checkTime:time,filterTags:['Gochara']}, key, 400],
    ];
    for (const [operation, body, token, status] of invalid) assert.equal((await call(operation, body, token)).status, status, operation + ':' + JSON.stringify(body));
  } finally { child.kill(); }
});
