#!/usr/bin/env python3
"""Run existing Aquarius examples/projects on Linux, with source-free ELF exports.

Run after scripts/publish-apphost.sh and dotnet build AquariusLang.sln.
Sibling projects are read only; all generated files go to .web-build/linux-projects.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / '.web-build/linux-projects'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cli', type=Path, default=ROOT / 'AquariusCli/bin/linux/Debug/net8.0/aqua.dll')
parser.add_argument('--projects-root', type=Path, default=ROOT.parent)
parser.add_argument('--executables', action='store_true')
parser.add_argument('--examples', action='store_true', help='Also run repository examples as source and source-free Wasm')
parser.add_argument('--resume', action='store_true', help='Reuse successful checks in the existing report')
parser.add_argument('--only', nargs='*', help='Limit to the named projects, e.g. marble painter snake')
args = parser.parse_args()
OUT.mkdir(parents=True, exist_ok=True)
cli = ['dotnet', str(args.cli.resolve())]
env = dict(os.environ, AQUARIUS_BUILD_TARGETS=str(ROOT / 'build-targets'))
report = OUT / 'results.json'
results = json.loads(report.read_text(encoding='utf-8')) if args.resume and report.exists() else []
# These fixtures exercise Android's host and camera APIs, not desktop contracts.
results = [row for row in results if '/android_smoke/' not in row['name']]


def run(command, name, frames=0, capture=False, expected=None, cwd=OUT, expected_code=0):
    if args.resume and any(row['name'] == name and row['passed'] for row in results):
        return True
    results[:] = [row for row in results if row['name'] != name]
    settings = dict(env, AQUARIUS_GRAPHICS_FRAMES=str(frames))
    settings.pop('AQUARIUS_GRAPHICS_CAPTURE', None)
    image = OUT / (name.replace('/', '-') + '.png')
    if capture:
        image.unlink(missing_ok=True)
        settings['AQUARIUS_GRAPHICS_CAPTURE'] = str(image)
    try:
        result = subprocess.run(command, cwd=cwd, env=settings, capture_output=True, text=True, timeout=240)
        passed = result.returncode == expected_code and (not capture or image.is_file())
        if expected:
            passed = passed and expected in result.stdout
        row = dict(name=name, passed=passed, code=result.returncode, output=result.stdout, error=result.stderr)
    except (subprocess.TimeoutExpired, OSError) as error:
        row = dict(name=name, passed=False, error=str(error))
    results.append(row)
    report.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print(('PASS ' if row['passed'] else 'FAIL ') + name, flush=True)
    if not row['passed']:
        print(row.get('output', '')[-2500:] + row.get('error', '')[-2500:], flush=True)
    return row['passed']


projects = [
    ('marble', 'AquariusLang_MarbleRun_3D', 'main.aqua', [], [('smoke.aqua', '整合驗證通過')]),
    ('painter', 'AquariusLang_Painter', '星泉塗鴉室.aqua', [], []),
    ('snake', 'AquariusLang_Snake', '貪吃蛇.aqua', [], [('規則驗證.aqua', '236')]),
    ('pbr', 'AquariusLang_PBR_ModelViewer', 'main.aqua', ['assets'], [('gpu-smoke.aqua', 'PBR GPU 驗證通過')]),
    ('audio', 'AquariusLangTW_AudioExamples', '星泉聲音工坊.aqua', ['音色'], [('音訊驗證.aqua', '音訊驗證全部通過')]),
    ('mediapipe', 'AquariusLangTW_MediaPipeExamples', '星泉視覺實驗室.aqua', ['模型'],
     [('姿態驗證.aqua', '模型推論驗證通過'), ('臉部驗證.aqua', '模型推論驗證通過'),
      ('圖形加速驗證.aqua', '圖形加速姿態推論驗證通過'), ('臉部圖形加速驗證.aqua', '圖形加速臉部推論驗證通過')]),
    ('kanban', 'AquariusLangTW_SingleUserKanban', '啟動看板.aqua', [], []),
    ('patchcore', 'AquariusLangTW_PatchCore', '主程式.aqua', ['資源'],
     [('單元測試.aqua', None), ('GPU驗證.aqua', None), ('ONNX加速驗證.aqua', None)]),
    ('advanced-pbr', 'AquariusLangTW_Advanced_PBR_Example', 'main.aqua',
     ['assets/original/meshes/skybox.obj', 'assets/baked/*.gz', 'assets/baked/*.aqmesh', 'assets/*.wgsl'], [('驗證.aqua', None)]),
    ('sponza', 'AquariusLangTW_Sponza_Atrium', 'main.aqua',
     ['assets/baked/*.gz', 'assets/baked/*.aqmesh', 'assets/*.wgsl', 'ASSET-LICENSES.md',
      'assets/original/LICENSE.upstream.md', 'assets/original/LicenseRef-CRYENGINE-Agreement.txt'], [('驗證.aqua', None)]),
]
excluded = {'.git', '.idea', 'web', 'dist', '網頁', '建置', 'node_modules'}
for name, folder, entry, assets, checks in projects:
    if args.only is not None and name not in args.only:
        continue
    source = args.projects_root / folder
    if not source.is_dir():
        print('SKIP missing sibling project: ' + str(source), flush=True)
        continue
    sources = sorted(p for p in source.rglob('*.aqua') if not excluded.intersection(p.relative_to(source).parts))
    wasm = OUT / (name + '.wasm')
    project = (source / 'aqua.toml').is_file()
    command = cli + (['build', str(source), '--platform', 'linux', '-o', str(wasm)] if project else
                     ['build', *map(str, sources), '--root', str(source), '--entry', entry, '-o', str(wasm)])
    if not project:
        for pattern in assets:
            for asset in sorted(source.glob(pattern)):
                command += ['--assets', str(asset)]
    if not run(command, name + '/compile'):
        continue
    frames = 1 if name in ('painter', 'kanban') else 2
    run(cli + ['run', str(wasm)], name + '/wasm', frames=frames, capture=name != 'patchcore')
    run(cli + ['build', str(source if project else wasm), '--target', 'web', '-o', str(OUT / (name + '-web'))], name + '/web-export')
    for check, expected in checks:
        run(cli + ['run', str(wasm), '--entry', check], name + '/' + check, expected=expected)
    if args.executables:
        # The relocated executable is the only application file in its directory.
        destination = OUT / (name + '-relocated')
        destination.mkdir(exist_ok=True)
        executable = destination / '星泉 app'
        if run(cli + ['build', str(wasm), '--target', 'linux', '-o', str(executable)], name + '/elf-export'):
            run([str(executable)], name + '/elf-run', frames=frames, capture=name != 'patchcore', cwd=destination)

if args.examples:
    desktop = ROOT / 'AquariusDesktop/bin/linux/Debug/net8.0/AquariusDesktop.dll'
    for name, original in [('examples', ROOT / 'examples'), ('desktop-examples', ROOT / 'AquariusDesktop/examples')]:
        with tempfile.TemporaryDirectory(prefix=name + '-', dir=OUT) as temporary:
            source = Path(temporary) / 'source'
            shutil.copytree(original, source, ignore=shutil.ignore_patterns('.idea'))
            # The repository's MediaPipe sample downloads these on first build.
            # Reuse the already prepared sibling models in the disposable copy.
            model_directory = source / 'mediapipe/模型'
            if model_directory.is_dir():
                for model in ['人物偵測.onnx', '姿態.onnx']:
                    prepared = args.projects_root / 'AquariusLangTW_MediaPipeExamples/模型' / model
                    if not (model_directory / model).exists() and prepared.exists():
                        shutil.copy2(prepared, model_directory / model)
                        results[:] = [row for row in results if row['name'] != name + '/compile']
            sources = sorted(p for p in source.rglob('*.aqua')
                             if not any((source / parent / 'aqua.toml').is_file()
                                        for parent in p.relative_to(source).parents))
            wasm = OUT / (name + '.wasm')
            if not run(cli + ['build', *map(str, sources), '--root', str(source), '--assets', str(source),
                              '--entry', 'increment.aqua', '-o', str(wasm)], name + '/compile'):
                continue
            entries = [p.relative_to(source).as_posix() for p in sources]
            for mode in ['source', 'wasm']:
                if mode == 'wasm':
                    shutil.rmtree(source)
                for entry in entries:
                    if entry.startswith('android_smoke/'):
                        continue
                    if entry in ['webcam/main.aqua', 'webcam/影格驗證.aqua']:
                        continue
                    frames = 1 if entry.startswith('color_mapping/') else 12 if entry.endswith('smoke.aqua') else 2
                    command = ['dotnet', str(desktop), str(source / entry)] if mode == 'source' else cli + ['run', str(wasm), '--entry', entry]
                    expected_code = 1 if entry.startswith('generate_errors/') else 0
                    # Direct source imports retain their existing working-directory
                    # contract. Compiled modules resolve through their package context.
                    run(command, f'{name}/{mode}/{entry}', frames=frames, expected_code=expected_code,
                        cwd=source / Path(entry).parent if mode == 'source' else OUT)

if not results:
    raise SystemExit('No projects selected/found.')
failed = sum(not row['passed'] for row in results)
print(f'{len(results) - failed}/{len(results)} checks passed; report: {OUT / "results.json"}')
raise SystemExit(1 if failed else 0)
