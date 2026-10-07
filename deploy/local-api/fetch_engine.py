"""Fetch only hash-locked official library files; apply documented infrastructure updates."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import shutil
import time
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
REVISION = '40763952742f76369a505d8db2e9e9fa67f75d78'


def fetch_engine(destination):
    lock = json.loads((ROOT / 'source-lock.json').read_text(encoding='utf-8'))
    if lock['revision'] != REVISION:
        raise ValueError('Unexpected source revision')
    destination = destination.resolve()
    source = destination / '.verified-source'

    def fetch(item):
        relative = Path(item['path'])
        if relative.is_absolute() or '..' in relative.parts or relative.parts[0] != 'Library':
            raise ValueError('Invalid source path')
        target = source / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        data = target.read_bytes() if target.exists() else b''
        if hashlib.sha256(data).hexdigest() != item['sha256']:
            url = f'https://raw.githubusercontent.com/VedAstro/VedAstro/{REVISION}/{item["path"]}'
            for attempt in range(3):
                try:
                    with urlopen(Request(url, headers={'User-Agent': 'VedAstro-local-build'}), timeout=30) as response:
                        data = response.read(item['bytes'] + 1)
                    break
                except OSError:
                    if attempt == 2:
                        raise
                    time.sleep(attempt + 1)
            if len(data) != item['bytes'] or hashlib.sha256(data).hexdigest() != item['sha256']:
                raise ValueError('Source hash mismatch')
            target.write_bytes(data)
        output = destination / relative
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(target, output)

    with ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(fetch, lock['files']))
    project_path = destination / 'Library/Library.csproj'
    project = ET.parse(project_path)
    project.find('./PropertyGroup/TargetFramework').text = 'net10.0'
    for reference in project.findall('.//PackageReference'):
        version = {'Microsoft.Extensions.Caching.Memory': '10.0.12',
                   'Microsoft.JSInterop': '10.0.12', 'Newtonsoft.Json': '13.0.4'}.get(reference.get('Include'))
        if version:
            reference.set('Version', version)
    group = ET.SubElement(project.getroot(), 'ItemGroup')
    ET.SubElement(group, 'Compile', {'Remove': 'Logic/CacheManager.cs'})
    properties = ET.SubElement(project.getroot(), 'PropertyGroup')
    ET.SubElement(properties, 'RestorePackagesWithLockFile').text = 'true'
    project.write(project_path, encoding='unicode')
    shutil.copyfile(ROOT / 'BoundedEngineCache.cs', destination / 'Library/BoundedEngineCache.cs')
    dependency_lock = ROOT / 'engine-packages.lock.json'
    if dependency_lock.exists():
        shutil.copyfile(dependency_lock, destination / 'Library/packages.lock.json')
    print(json.dumps({'revision': REVISION, 'verified_source_files': len(lock['files'])}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--destination', type=Path, default=ROOT / '.engine')
    fetch_engine(parser.parse_args().destination)
