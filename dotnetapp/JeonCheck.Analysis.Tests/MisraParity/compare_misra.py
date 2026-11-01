"""원본 misra.py와 C# 이식(MisraChecker)의 결과를 dump 파일 단위로 비교한다.

사용 (저장소 루트, Windows):
  1) 비교할 C 소스로 dump를 만든다: tools/cppcheck/cppcheck.exe --dump <파일 또는 폴더>
  2) python dotnetapp/JeonCheck.Analysis.Tests/MisraParity/compare_misra.py <dump가 있는 폴더>
규칙·파일·줄·열이 하나라도 다르면 차이를 출력하고 종료 코드 1을 반환한다.
"""
import glob, json, os, subprocess, sys
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
MISRA = os.path.join(ROOT, "tools", "cppcheck", "addons", "misra.py")
TESTS = os.path.join(ROOT, "dotnetapp", "JeonCheck.Analysis.Tests")


def run_original(dump):
    r = subprocess.run([sys.executable, MISRA, "--cli", dump], capture_output=True, text=True, encoding="utf-8")
    out = []
    for line in r.stdout.splitlines():
        if line.startswith("{"):
            d = json.loads(line)
            if d.get("errorId") != "logChecker" and "summary" not in d:
                out.append((f"{d['addon']}-{d['errorId']}", d["file"], d["linenr"], d["column"]))
    return dump, out


def main():
    folder = os.path.abspath(sys.argv[1])
    dumps = sorted(glob.glob(os.path.join(folder, "**", "*.dump"), recursive=True))
    with ThreadPoolExecutor(os.cpu_count()) as ex:
        original = dict(ex.map(run_original, dumps))
    out_json = os.path.join(folder, "misra_port.json")
    subprocess.run(["dotnet", "run", "--project", TESTS, "--", "--misra-json", folder, out_json], check=True)
    port = {os.path.join(folder, k): [tuple(x) for x in v] for k, v in json.load(open(out_json, encoding="utf-8")).items()}
    missing, extra = defaultdict(int), defaultdict(int)
    for dump in dumps:
        a, b = Counter(original.get(dump, [])), Counter(port.get(dump, []))
        for k, n in (a - b).items():
            missing[k[0]] += n
            print("원본에만:", os.path.relpath(dump, folder), k)
        for k, n in (b - a).items():
            extra[k[0]] += n
            print("이식에만:", os.path.relpath(dump, folder), k)
    total = sum(len(v) for v in original.values())
    print(f"dump {len(dumps)}개, 원본 위반 {total}건, 원본에만 {sum(missing.values())}건, 이식에만 {sum(extra.values())}건")
    return 1 if missing or extra else 0


if __name__ == "__main__":
    sys.exit(main())
