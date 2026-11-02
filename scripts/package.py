from pathlib import Path
import hashlib
import shutil
import zipfile

root = Path(__file__).resolve().parent.parent
out = root / 'outputs'
source = out / 'RelayProxy-source.zip'
with zipfile.ZipFile(source, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for name in ('README.md', 'LICENSE.txt', '.gitignore'):
        archive.write(root / name, 'RelayProxy-source/' + name)
    for directory in ('RelayProxy', 'tests', 'ui-tests', 'scripts'):
        for file in (root / directory).rglob('*'):
            relative = file.relative_to(root)
            if not file.is_file() or any(part in ('bin', 'obj', '__pycache__') for part in relative.parts):
                continue
            if file.suffix in ('.exe', '.dll', '.gz', '.pyc'):
                continue
            archive.write(file, 'RelayProxy-source/' + relative.as_posix())
shutil.copy2(source, out / 'RelayProxy' / source.name)
shutil.copy2(out / 'test-results.txt', out / 'RelayProxy' / 'test-results.txt')
package = out / 'RelayProxy-2.0-win-x64.zip'
with zipfile.ZipFile(package, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for file in (out / 'RelayProxy').rglob('*'):
        if file.is_file():
            archive.write(file, file.relative_to(out).as_posix())
with zipfile.ZipFile(package) as archive:
    assert archive.testzip() is None
    assert 'RelayProxy/engine/sing-box.exe' in archive.namelist()
    assert not any('profile.json' in name for name in archive.namelist())
digest = hashlib.sha256(package.read_bytes()).hexdigest()
package.with_suffix('.sha256').write_text(digest + '  ' + package.name + '\n')
print(f'Verified {package.name}: {package.stat().st_size / 1024 / 1024:.1f} MiB')
