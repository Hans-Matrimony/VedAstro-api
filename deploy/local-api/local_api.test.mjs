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
      ...['marriage', 'career', 'education'].map(topic => ['ReadingEvidence', { time, topic,
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
      if (operation === 'ReadingEvidence') {
        const evidence = first.data.Payload.ReadingEvidence;
        assert.equal(evidence.topic, body.topic);
        assert.equal(evidence.interpretationHouseSystem, 'whole_sign');
        assert.equal(evidence.strength.nativeHouseSystem, 'vedastro_bhava');
        const sum = Object.values(evidence.strength.componentsVirupas).reduce((a, b) => a + b, 0);
        assert.ok(Math.abs(sum - evidence.strength.totalVirupas) <= 0.011);
        assert.equal(evidence.eventTimingAvailable, false);
        assert.equal(Object.keys(evidence.natal.planets).length, 9);
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
    ];
    for (const [operation, body, token, status] of invalid) assert.equal((await call(operation, body, token)).status, status, operation + ':' + JSON.stringify(body));
  } finally { child.kill(); }
});
