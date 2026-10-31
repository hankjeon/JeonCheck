# 03. 데이터 및 파일 포맷 명세서 (Data & File Specification)

`전검사(JeonCheck)`는 하드코딩을 피하고 사용자가 타겟 정보나 한글 규칙 설명을 쉽게 확인·수정할 수 있도록 모든 설정 및 규칙 데이터를 JSON 및 XML 파일로 관리한다.

---

## 1. 타겟 프리셋 명세 (`data/targets/`)

### 1.1 타겟 목록 정의 (`data/targets/targets.json`)
각 임베디드 프로세서 타겟의 메타데이터, Cppcheck 플랫폼 XML 파일 경로, 컴파일러 특수 문법 무시/치환용 매크로(`-D`) 목록을 정의한다.

```json
{
  "targets": [
    {
      "id": "ti-c6746-eabi",
      "name": "TI DSP TMS320C6746 (EABI 32-bit long)",
      "vendor": "Texas Instruments",
      "architecture": "C674x DSP | 32-bit Little Endian | int:32b, long:32b, ptr:32b",
      "endian": "little",
      "pointerBits": 32,
      "intBits": 32,
      "longBits": 32,
      "platformXml": "ti_c6000_eabi.xml",
      "clangTarget": "armv7r-none-eabi",
      "defaultStd": "c99",
      "defines": [
        "_TMS320C6X=1",
        "_TMS320C6740=1",
        "__TI_EABI__=1",
        "cregister=",
        "interrupt=",
        "far=",
        "near=",
        "__far=",
        "__near=",
        "restrict="
      ],
      "description": "TI C6746 고정/부동소수점 DSP (EABI 모드: int 32-bit, long 32-bit, pointer 32-bit)"
    }
  ]
}
```

### 1.2 지원 타겟 플랫폼 XML 목록 (`data/targets/*.xml`)
Cppcheck의 `--platform=` 인자로 전달되는 XML 파일로, 각 타겟의 정확한 자료형 바이트 크기와 엔디안을 정의한다.

| 파일명 | 대상 타겟 | Endian | `char` | `short` | `int` | `long` | `long long` | `pointer` |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| `ti_c6000_eabi.xml` | TI `6746`, `6655`, `6672`, `6678` (EABI) | Little | 1B (8b) | 2B (16b) | 4B (32b) | **4B (32b)** | 8B (64b) | 4B (32b) |
| `ti_c6000_coff.xml` | TI `6746`, `6655`, `6672`, `6678` (COFF) | Little | 1B (8b) | 2B (16b) | 4B (32b) | **5B (40b)** | 8B (64b) | 4B (32b) |
| `nxp_t1024_be32.xml` | NXP `QorIQ T1024` (e5500 32-bit mode) | **Big** | 1B (8b) | 2B (16b) | 4B (32b) | 4B (32b) | 8B (64b) | 4B (32b) |
| `nxp_t1024_be64.xml` | NXP `QorIQ T1024` (e5500 64-bit mode) | **Big** | 1B (8b) | 2B (16b) | 4B (32b) | **8B (64b)** | 8B (64b) | **8B (64b)** |
| `polarfire_rv64.xml` | Microchip `PolarFire SoC` (RISC-V 64) | Little | 1B (8b) | 2B (16b) | 4B (32b) | **8B (64b)** | 8B (64b) | **8B (64b)** |
| `versal_ap_a72.xml` | AMD `Versal AP` (Cortex-A72 64-bit) | Little | 1B (8b) | 2B (16b) | 4B (32b) | **8B (64b)** | 8B (64b) | **8B (64b)** |
| `versal_rp_r5f.xml` | AMD `Versal RP` (Cortex-R5F 32-bit) | Little | 1B (8b) | 2B (16b) | 4B (32b) | 4B (32b) | 8B (64b) | 4B (32b) |

---

## 2. 한글 규칙 해설 데이터베이스 (`data/rules_ko.json`)

모든 검사 규칙(자체 타입 변환 규칙, MISRA C 2012 규칙, Cppcheck 내장 결함 ID)의 한글 설명과 기본 활성화 여부를 정의한다.

```json
{
  "rules": [
    {
      "id": "implicitNarrowing",
      "category": "타입 및 형변환",
      "severity": "Warning",
      "obligation": "필수(Required)",
      "defaultEnabled": true,
      "title": "암묵적 축소 변환 (Implicit Narrowing Conversion)",
      "summary": "명시적 캐스트 없이 큰 자료형의 값을 더 작은 자료형에 대입하거나 전달함",
      "description": "더 넓은 비트 폭을 가진 정수나 부동소수점 값을 더 좁은 비트 폭의 변수에 암묵적으로 대입할 경우 상위 비트가 잘려나가(Truncation) 센서 데이터나 제어 연산 결과가 심각하게 왜곡될 수 있습니다. MISRA C 2012 Rule 10.3과 직결되는 핵심 안전 규칙입니다.",
      "badExample": "int32_t raw_adc = read_sensor();\nint16_t calibrated = raw_adc; // 경고: 32-bit -> 16-bit 암묵적 축소 변환",
      "goodExample": "int32_t raw_adc = read_sensor();\nif (raw_adc >= INT16_MIN && raw_adc <= INT16_MAX) {\n    int16_t calibrated = (int16_t)raw_adc; // 범위 확인 후 명시적 캐스트\n}"
    }
  ]
}
```

- **등록 범위**: 자체 타입 변환 규칙과 MISRA C 2012 규칙 외에, 내장 도구가 C 코드에서 보고할 수 있는 진단 ID(Cppcheck 결함 ID, Clang-Tidy `bugprone-*`/`cert-*`, Clang 정적 분석기 `clang-analyzer-*`)를 미리 등록하여 룰셋 창에서 개별 ON/OFF 및 프로젝트 저장이 가능하도록 한다. C++ 전용 검사와 진단을 내지 않는 내부 모델링 체커는 등록하지 않는다.
- **코드 예시**: `badExample`/`goodExample`은 비워 둘 수 있으며, 비어 있으면 룰셋 창에 "등록된 코드 예시 없음" 안내가 표시된다.
- **미등록 ID**: 도구 버전 변경 등으로 등록되지 않은 진단 ID가 보고되면 임시 규칙으로 결함 목록에 표시되며, 룰셋 창에서는 관리되지 않는다. 필요 시 이 파일에 항목을 추가한다.
- **Clang-Tidy 별칭**: 하나의 진단에 여러 ID가 붙는 경우(예: `bugprone-signed-char-misuse`와 `cert-str34-c`) 켜져 있는 등록 ID로 표시하고, 등록된 ID가 모두 꺼져 있으면 표시하지 않는다.

---

## 3. 프로젝트 파일 포맷 (`*.jcproj`)

사용자가 구성한 프로젝트 환경과 룰셋 선택 상태를 JSON 형식의 `*.jcproj` 파일로 저장한다. (구현: `Models/ProjectFileModel.cs`, `Services/DataRepository.cs`)

```json
{
  "projectName": "FlightControl_DSP",
  "version": "1.0",
  "savedAt": "2026-10-31T15:00:00+09:00",
  "targetPresetId": "ti-c6678-eabi",
  "cStandard": "c99",
  "customDefines": "BOARD_REV=2, USE_DMA=1",
  "forceAllIfdefConfigs": false,
  "enableClangTidy": true,
  "targetPaths": [
    "src"
  ],
  "nonTargetPaths": [
    "include",
    "drivers/ti_csl",
    "src/legacy"
  ],
  "excludedTargetPaths": [
    "src/legacy",
    "src/test_stub.c"
  ],
  "excludedNonTargetPaths": [
    "drivers/ti_csl/unused_module.h"
  ],
  "enabledRuleIds": [
    "implicitNarrowing",
    "implicitSignConversion",
    "misra-c2012-10.3",
    "misra-c2012-11.4",
    "arrayIndexOutOfBounds",
    "nullPointer",
    "uninitvar"
  ],
  "disabledRuleIds": [
    "misra-c2012-15.1",
    "explicitNarrowingCast"
  ],
  "suppressedIssues": [
    {
      "ruleId": "misra-c2012-11.4",
      "filePath": "src/control_loop.c",
      "line": 45,
      "message": "포인터와 정수 타입 간의 형변환('unsigned int (32-bit)' -> 'unsigned int* (32-bit ptr)')이 사용되었습니다."
    }
  ]
}
```

| 필드 | 설명 |
| :--- | :--- |
| `projectName` / `version` / `savedAt` | 프로젝트 이름(저장 파일명), 파일 형식 버전, 저장 시각 |
| `targetPresetId` | `targets.json`의 타겟 프리셋 `id` (없으면 첫 번째 프리셋으로 대체) |
| `cStandard` | C 표준 (`c89`, `c99`, `c11`, `c17`) |
| `customDefines` | 사용자 정의 매크로 문자열. 쉼표·세미콜론·공백으로 구분하며 `-D` 접두어는 생략 가능 |
| `forceAllIfdefConfigs` | 모든 `#ifdef` 분기 강제 검사(Cppcheck `--force`) 여부 |
| `enableClangTidy` | Clang-Tidy 보조 분석 실행 여부 |
| `targetPaths` | 검사 대상 트리의 최상위(루트) 폴더/파일 경로 |
| `nonTargetPaths` | 비대상(참조) 트리의 최상위(루트) 폴더/파일 경로 |
| `excludedTargetPaths` | 검사 대상 루트 폴더 아래에서 사용자가 제거하거나 비대상으로 옮긴 하위 파일/폴더. 불러올 때 루트 폴더를 다시 스캔해도 이 항목은 대상 트리에 포함되지 않는다 |
| `excludedNonTargetPaths` | 비대상 루트 폴더 아래에서 사용자가 제거하거나 검사 대상으로 옮긴 하위 파일/폴더 |
| `enabledRuleIds` | 저장 시점에 활성화(ON)된 규칙 ID 목록 |
| `disabledRuleIds` | 저장 시점에 비활성화(OFF)된 규칙 ID 목록. 불러올 때 두 목록 중 어디에도 없는 규칙(저장 이후 `rules_ko.json`에 새로 추가된 규칙)은 규칙 DB의 `defaultEnabled` 값을 따른다 |
| `suppressedIssues` | 개별 예외(무시) 처리 항목. `ruleId` + `filePath` + `line` 조합으로 결함을 식별하며, `message`는 예외 처리 당시의 검출 메시지(표시용) |

- **폴더 트리 복원 방식**: 불러오기 시 `targetPaths`/`nonTargetPaths`의 각 루트 폴더를 디스크에서 다시 스캔하여 트리를 구성하고(`.c`, `.h`, `.cpp`, `.hpp`만 포함), `excluded*Paths`에 기록된 하위 항목은 제외한다. 대상 폴더의 일부를 비대상으로 옮긴 경우 해당 경로는 `excludedTargetPaths`와 `nonTargetPaths`에 함께 기록된다.
- **경로 저장 원칙**: `.jcproj` 파일이 위치한 폴더 기준의 상대 경로를 우선 저장하여 프로젝트 폴더 이동 시에도 호환성을 유지하며, 다른 드라이브 등 상대 경로 변환이 불가능한 경우 절대 경로를 사용한다. (모든 경로 필드에 동일 적용)
- **하위 호환**: `excludedTargetPaths`, `excludedNonTargetPaths`가 없는 이전 버전 파일도 그대로 불러올 수 있다 (제외 항목 없음으로 처리). `disabledRuleIds`가 없는 이전 버전 파일은 `enabledRuleIds`에 있는 규칙만 켜고 나머지는 모두 끈 상태로 불러온다.
