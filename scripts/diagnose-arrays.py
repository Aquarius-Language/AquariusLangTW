"""Compare preserved Aquarius with the new runtime and Chinese array APIs (Python standard library only)."""
from __future__ import annotations
import argparse
from datetime import datetime, timedelta, timezone
import json
import math
from pathlib import Path
import random
import statistics
import subprocess
import sys


def migrate(source: str, name: str) -> str:
    """Change initialization only; retain each benchmark's computational algorithm."""
    replacements = {
        "sieve": [("變數 flags = [];\n迴圈 (變數 i = 0; i <= benchmark_n; i++) { flags = 加入(flags, 真); }", "變數 flags = 陣列.建立(benchmark_n + 1, 真);")],
        "quicksort": [("變數 values = [];", "變數 values = 陣列.建立(0, 0, benchmark_n);"), ("values = 加入(values, seed);", "陣列.追加(values, seed);")],
        "matrix": [("變數 a = []; 變數 b = [];", "變數 a = 陣列.建立(benchmark_n * benchmark_n, 0);\n變數 b = 陣列.建立(benchmark_n * benchmark_n, 0);"), ("a = 加入(a, x - (x / 7) * 7);", "a[i * benchmark_n + j] = x - (x / 7) * 7;"), ("b = 加入(b, y - (y / 5) * 5);", "b[i * benchmark_n + j] = y - (y / 5) * 5;")],
        "spectral_norm": [("變數 u = []; 變數 v = []; 變數 temp = [];\n迴圈 (變數 i = 0; i < benchmark_n; i++) {\n    u = 加入(u, 1.0d); v = 加入(v, 0.0d); temp = 加入(temp, 0.0d);\n}", "變數 u = 陣列.建立(benchmark_n, 1.0d);\n變數 v = 陣列.建立(benchmark_n, 0.0d);\n變數 temp = 陣列.建立(benchmark_n, 0.0d);")],
        "nqueens": [("變數 board = [];\n迴圈 (變數 i = 0; i < benchmark_n; i++) { board = 加入(board, 0); }", "變數 board = 陣列.建立(benchmark_n, 0);")],
    }
    for old, new in replacements.get(name, []):
        if source.count(old) != 1:
            raise ValueError(f"Unexpected benchmark source for {name}; refusing an ambiguous edit")
        source = source.replace(old, new)
    return source


def microbenchmarks():
    timer = "變數 start=目前時間();\n"
    finish = '\n印出("RESULT",result);印出("ELAPSED_MS",目前時間()-start);\n'
    return {
        "array_make": (250000,
            timer + "變數 result=0;迴圈(變數 round=0;round<10;round++){變數 a=[];迴圈(變數 i=0;i<25000;i++){a=加入(a,7);}result+=長度(a);}" + finish,
            timer + "變數 result=0;迴圈(變數 round=0;round<10;round++){變數 a=陣列.建立(25000,7);result+=陣列.長度(a);}" + finish),
        "array_append": (250000,
            timer + "變數 result=0;迴圈(變數 round=0;round<10;round++){變數 a=[];迴圈(變數 i=0;i<25000;i++){a=加入(a,i);}result+=長度(a);}" + finish,
            timer + "變數 result=0;迴圈(變數 round=0;round<10;round++){變數 a=陣列.建立(0,0,25000);迴圈(變數 i=0;i<25000;i++){陣列.追加(a,i);}result+=陣列.長度(a);}" + finish),
        "array_copy": (2499900,
            "變數 src=[];變數 dst=[];迴圈(變數 i=0;i<25000;i++){src=加入(src,i);dst=加入(dst,0);}\n" + timer + "變數 result=0;迴圈(變數 round=0;round<100;round++){迴圈(變數 i=0;i<25000;i++){dst[i]=src[i];}result+=dst[24999];}" + finish,
            "變數 src=陣列.建立(25000,0);變數 dst=陣列.建立(25000,0);迴圈(變數 i=0;i<25000;i++){src[i]=i;}\n" + timer + "變數 result=0;迴圈(變數 round=0;round<100;round++){陣列.複製(dst,src);result+=dst[24999];}" + finish),
        "array_fill": (17500000,
            "變數 a=[];迴圈(變數 i=0;i<25000;i++){a=加入(a,0);}\n" + timer + "變數 result=0;迴圈(變數 round=0;round<100;round++){迴圈(變數 i=0;i<25000;i++){a[i]=7;}result+=a[24999]*長度(a);}" + finish,
            "變數 a=陣列.建立(25000,0);\n" + timer + "變數 result=0;迴圈(變數 round=0;round<100;round++){陣列.填入(a,7);result+=a[24999]*陣列.長度(a);}" + finish),
    }


def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--benchmark-root",type=Path,required=True)
    parser.add_argument("--before",type=Path,required=True)
    parser.add_argument("--after",type=Path,required=True)
    parser.add_argument("--original-scripts",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--repetitions",type=int,default=7)
    parser.add_argument("--stage-only",action="store_true")
    args=parser.parse_args(argv)
    if args.repetitions<1: parser.error("repetitions must be positive")
    args.output.mkdir(parents=True,exist_ok=True)
    sys.path.insert(0,str(args.benchmark_root.resolve()))
    from run_benchmarks import environment_info, execute, runtime_info, sha256, summarize
    from reference import expected
    manifest=json.loads((args.benchmark_root/"benchmarks.json").read_text(encoding="utf-8"))
    scripts=args.output/"scripts-after";scripts.mkdir(exist_ok=True)
    originals={}
    for name in manifest:
        originals[name]=(args.original_scripts/(name+".aqua")).read_text(encoding="utf-8")
        (scripts/(name+".aqua")).write_text(migrate(originals[name],name),encoding="utf-8")
    if args.stage_only: return 0
    clock=timezone(timedelta(hours=8),"Asia/Taipei")
    report={"started_at":datetime.now(clock).isoformat(),"environment":environment_info(),
        "method":"Precompile each case; one untimed process warmup then seven shuffled rounds by default. Workload timers include initialization/computation and exclude process startup, compilation and printing. Each sample uses a fresh process/engine; warmup does not reuse the Wasm engine. Original/new-api scripts have identical algorithms and validated results. Microbenchmarks replace only the measured array operation.",
        "config":{"repetitions":args.repetitions,"warmups":1,"seed":20261014,"profile":"standard"},
        "runtimes":{"before":runtime_info("aquarius",str(args.before.resolve())),"after":runtime_info("aquarius",str(args.after.resolve()))},
        "cases":{},"execution_order":[],"benchmarks":manifest,"harness_sha256":sha256(__file__)}
    probes={name:(expected(name,manifest[name]["standard"]),source,(scripts/(name+".aqua")).read_text(encoding="utf-8")) for name,source in originals.items()}
    probes.update(microbenchmarks())
    for name,(answer,old,new) in probes.items():
        for variant,compiler,source in [("before",args.before,old),("core_only",args.after,old),("array_api",args.after,new)]:
            directory=args.output/"staged"/variant;directory.mkdir(parents=True,exist_ok=True)
            aqua=directory/(name+".aqua");wasm=aqua.with_suffix(".wasm");aqua.write_text(source,encoding="utf-8")
            built=subprocess.run([str(compiler.resolve()),"build",str(aqua.resolve()),"--root",str(directory.resolve()),"-o",str(wasm.resolve())],capture_output=True,text=True,encoding="utf-8",timeout=120)
            if built.returncode: raise RuntimeError(f"Build failed for {name}/{variant}: {built.stderr}")
            report["cases"][name+"/"+variant]={"benchmark":name,"variant":variant,"expected":answer,"command":[str(compiler.resolve()),"run",str(wasm.resolve())],"source_sha256":sha256(aqua),"wasm_sha256":sha256(wasm),"warmups":[],"samples":[]}
    print("Compiled all variants; starting validated interleaved measurements",flush=True)
    rng=random.Random(20261014)
    for phase,count in [("warmups",1),("samples",args.repetitions)]:
        for round_number in range(count):
            keys=list(report["cases"]);rng.shuffle(keys)
            for key in keys:
                case=report["cases"][key];spec=manifest.get(case["benchmark"],{})
                sample=execute(case["command"],case["expected"],spec,120,args.output.resolve())
                sample["round"]=round_number+1;case[phase].append(sample)
                report["execution_order"].append({"case":key,"phase":phase,"round":round_number+1})
                if sample["status"]!="ok": raise RuntimeError(f"Invalid sample {key}: {sample}")
            print(f"{phase} round {round_number+1}/{count} validated",flush=True)
    for case in report["cases"].values():case["summary"]=summarize(case,args.repetitions)
    report["comparison"]={}
    for name in probes:
        medians={variant:report["cases"][name+"/"+variant]["summary"]["median_ms"] for variant in ["before","core_only","array_api"]}
        report["comparison"][name]={**medians,"core_speedup":medians["before"]/medians["core_only"],"api_speedup":medians["core_only"]/medians["array_api"],"total_speedup":medians["before"]/medians["array_api"]}
    for group,names in [("suite",list(manifest)),("array_workloads",["sieve","quicksort","matrix","nqueens","spectral_norm"]),("micro",list(microbenchmarks()))]:
        report[group+"_geometric_speedup"]=math.exp(statistics.mean(math.log(report["comparison"][name]["total_speedup"]) for name in names))
    report["core_only_geometric_speedup"]=math.exp(statistics.mean(math.log(report["comparison"][name]["core_speedup"]) for name in manifest))
    report["valid"]=True;report["finished_at"]=datetime.now(clock).isoformat()
    (args.output/"comparison.json").write_text(json.dumps(report,indent=2,ensure_ascii=False)+"\n",encoding="utf-8")
    rows=["# 陣列效能診斷 / Array performance diagnosis", "", report["method"], "", "Measurements: medians in milliseconds. Speedup = before / after; < 1 means slower.", "", "| Case | Original runtime | New core, original script | New core + Chinese API | Core speedup | Total speedup |", "| --- | ---: | ---: | ---: | ---: | ---: |"]
    for name,row in report["comparison"].items(): rows.append(f"| {name} | {row['before']:.4f} | {row['core_only']:.4f} | {row['array_api']:.4f} | {row['core_speedup']:.3f}x | {row['total_speedup']:.3f}x |")
    rows.extend(["",f"All 10 suite cases geometric speedup: {report['suite_geometric_speedup']:.3f}x.",f"Five array workloads: {report['array_workloads_geometric_speedup']:.3f}x.",f"Four isolated array operations: {report['micro_geometric_speedup']:.3f}x.",f"Core changes alone, all 10 unchanged scripts: {report['core_only_geometric_speedup']:.3f}x.","","Each result was checked against the existing independent reference or a fixed exact microbenchmark checksum. Full samples, spread, execution order and binary/source hashes are in comparison.json. Small changes can be timing noise; initialization is a small fraction of matrix/spectral/nqueens, so their whole-workload gain can be small. These measurements are from this desktop; portable implementation is verified separately on JavaScript WebAssembly. Array Copy/Fill/Make replace per-element Aquarius loops with core Wasm operations; Append retains the original loop and avoids persistent array headers. Quicksort still uses the original middle-pivot quicksort."])
    (args.output/"README.md").write_text("\n".join(rows)+"\n",encoding="utf-8")
    print(json.dumps(report["comparison"],indent=2));return 0


if __name__=="__main__":raise SystemExit(main())
