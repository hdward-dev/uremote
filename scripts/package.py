"""Package the Release module output for AsterDock; run dotnet build first."""
from pathlib import Path
import fnmatch
import hashlib
import json
import zipfile

root = Path(__file__).resolve().parents[1]
source = root / 'src/URemote.Module/bin/Release/net10.0'
manifest = json.loads((source / 'app.json').read_text())
expected = json.loads((root / 'src/URemote.Module/app.json').read_text())
if manifest != expected:
    raise SystemExit('Module manifest is stale; rebuild Release first.')
shared = ['AsterDock.Contracts.dll', 'AsterDock.UI.dll', 'Avalonia*.dll',
          'HarfBuzzSharp.dll', 'Irihi.*.dll', 'MicroCom.Runtime.dll',
          'Semi.*.dll', 'SkiaSharp.dll', 'Tmds.DBus.Protocol.dll', 'Ursa*.dll']
out = root / 'artifacts/release'
out.mkdir(parents=True, exist_ok=True)
bundle = out / 'AsterDock-App-u-remote.appbundle'
with zipfile.ZipFile(bundle, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for path in sorted(source.rglob('*')):
        if not path.is_file():
            continue
        rel = path.relative_to(source)
        if len(rel.parts) > 1 and rel.parts[0] not in ('licenses', 'runtimes'):
            continue
        if path.suffix == '.pdb' or path.name in ('URemote.Host', 'URemote.Host.exe'):
            continue
        if any(fnmatch.fnmatch(path.name, pattern) for pattern in shared):
            continue
        if len(rel.parts) == 1 and path.suffix not in ('.dll', '.json', '.md'):
            continue
        archive.write(path, rel.as_posix())
with zipfile.ZipFile(bundle) as archive:
    assert archive.testzip() is None
    names = set(archive.namelist())
    required = {'app.json', manifest['entryAssembly'], 'URemote.Core.dll',
                'URemote.Host.dll', 'URemote.Linux.dll', 'URemote.Media.dll',
                'SIPSorcery.dll', 'Concentus.dll', 'THIRD-PARTY-NOTICES.md',
                'licenses/uurc-web-MIT.txt', 'licenses/SIPSorcery.md', 'licenses/Concentus.txt'}
    assert required <= names, required - names
    assert not any(n in names for n in ('identity.json', 'settings.json'))
    assert 'AsterDock.Contracts.dll' not in names
sha = hashlib.sha256(bundle.read_bytes()).hexdigest()
(out / 'SHA256SUMS.txt').write_text(f'{sha}  {bundle.name}\n')
print(f'Validated {len(names)} files, version {manifest["version"]}, {bundle.stat().st_size} bytes')
print(bundle)
