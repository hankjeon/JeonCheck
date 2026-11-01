# 03. 데이터 및 파일 포맷 명세서 (Data & File Specification)

`전검사(JeonCheck)`는 하드코딩을 피하고 사용자가 타겟 정보나 한글 규칙 설명을 쉽게 확인·수정할 수 있도록 타겟 프리셋·한글 해설·프로젝트 저장 데이터를 JSON 및 XML로 관리한다. 분석 알고리즘, UI 상태와 요약 집계는 C# 및 XAML에 구현되어 있다.

---

## 1. 타겟 프리셋 명세 (`data/targets/`)

### 1.1 타겟 목록 정의 (`data/targets/targets.json`)
각 임베디드 프로세서 타겟의 메타데이터, Cppcheck 플랫폼 XML 파일 경로, 컴파일러 특수 문법 무시/치환용 매크로(`-D`) 목록을 정의한다.

```json
{
  "targets": [
    {
      "id": "ti-c6746-eabi",
      "name": "TI DSP C6746 (EABI 32-bit long)",
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
        "_LITTLE_ENDIAN=1",
        "cregister=",
        "interrupt=",
        "far=",
        "near=",
        "__far=",
        "__near=",
        "restrict=",
        "__cregister="
      ],
      "description": "TI TMS320C6746 고정/부동소수점 DSP (EABI 표준: int 32-bit, long 32-bit, 포인터 32-bit)"
    }
  ]
}
```

### 1.2 지원 타겟 플랫폼 XML 목록 (`data/targets/*.xml`)
Cppcheck의 `--platform=` 인자로 전달되는 XML 파일로, 현재 분석 모델의 자료형 크기와 기본 char 부호를 정의한다. 아래 엔디안 열은 프리셋 메타데이터 기준이며 XML에 별도 endian 요소가 있는 것은 아니다. 실제 컴파일러 ABI와의 일치는 별도 검증이 필요하다.

| 파일명 | 대상 타겟 | Endian | `char` | `short` | `int` | `long` | `long long` | `pointer` |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| `ti_c6000_eabi.xml` | TI `6746`, `6655`, `6672`, `6678` (EABI) | Little | 1B (8b) | 2B (16b) | 4B (32b) | **4B (32b)** | 8B (64b) | 4B (32b) |
| `ti_c6000_coff.xml` | TI `6746`, `6678` (현재 COFF 프리셋) | Little | 1B (8b) | 2B (16b) | 4B (32b) | **5B (40b)** | 8B (64b) | 4B (32b) |
| `nxp_t1024_be32.xml` | NXP `QorIQ T1024` (e5500 32-bit mode) | **Big** | 1B (8b) | 2B (16b) | 4B (32b) | 4B (32b) | 8B (64b) | 4B (32b) |
| `nxp_t1024_be64.xml` | NXP `QorIQ T1024` (e5500 64-bit mode) | **Big** | 1B (8b) | 2B (16b) | 4B (32b) | **8B (64b)** | 8B (64b) | **8B (64b)** |
| `polarfire_rv64.xml` | Microchip `PolarFire SoC` (RISC-V 64) | Little | 1B (8b) | 2B (16b) | 4B (32b) | **8B (64b)** | 8B (64b) | **8B (64b)** |
| `versal_ap_a72.xml` | AMD `Versal AP` (Cortex-A72 64-bit) | Little | 1B (8b) | 2B (16b) | 4B (32b) | **8B (64b)** | 8B (64b) | **8B (64b)** |
| `versal_rp_r5f.xml` | AMD `Versal RP` (Cortex-R5F 32-bit) | Little | 1B (8b) | 2B (16b) | 4B (32b) | 4B (32b) | 8B (64b) | 4B (32b) |

### 1.3 프리셋 목록과 적용 제한

현재 `targets.json`은 11개 프리셋을 제공한다. TI C6655/C6672에는 EABI 프리셋만 있으며 COFF 선택 항목은 없다. `defaultStd`는 메타데이터로 로드되지만 타겟 선택 시 `SelectedCStandard`를 자동 변경하는 동작은 없다. UI의 초기 C 표준은 C99이고 프로젝트를 불러오면 저장된 값을 사용한다.

TI DSP의 `clangTarget`은 모두 `armv7r-none-eabi` 대체 모델이다. 특히 Cppcheck의 COFF `long=40`과 Clang의 ARM `long` 모델은 동일하지 않다. NXP는 `powerpc-none-eabihf`/`powerpc64-none-elf`, PolarFire는 `riscv64-unknown-elf`, Versal AP는 `aarch64-none-elf`, Versal RP는 `armv7r-none-eabi`를 사용한다.

Cppcheck 비트 폭은 플랫폼 XML과 생성된 dump에서, Clang-Tidy 비트 폭은 선택한 트리플과 내장 간이 헤더에서 결정된다. JSON의 `pointerBits`·`intBits`·`longBits`를 변경하는 것만으로 두 엔진의 타입 모델이 모두 바뀌지는 않는다.

---

## 2. 한글 규칙 해설 데이터베이스 (`data/rules_ko.json`)

현재 등록된 477개 진단 ID의 한글 설명과 기본 활성화 여부를 정의한다. 자체 타입 변환, MISRA 132개 ID(misra.py가 보고하는 131개와 Cppcheck 결과를 매핑하는 9.1. 두 앱 모두 misra.py 기준으로 보고), Cppcheck 및 Clang-Tidy/CERT/Clang Analyzer ID를 포함한다. DB 등록은 엔진 구현이나 해당 버전의 체커 지원을 보장하지 않는다.

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
      "summary": "암묵적 정수·실수 변환에서 비트 폭 또는 정밀도가 손실될 수 있음",
      "description": "더 넓은 타입의 값을 더 좁은 타입으로 대입·인자 전달·반환하면 값의 범위나 정밀도가 손실될 수 있습니다. 정수 축소는 상위 비트를 잃을 수 있고 실수 변환은 반올림이나 소수 부분 손실을 일으킬 수 있습니다. C# 분석기는 정수 축소, 실수 축소 및 실수→정수 변환을 검사하며, Clang-Tidy가 보고한 정수→실수 등의 정밀도 손실도 이 항목으로 통합 표시합니다. 원문 진단과 소스 타입을 확인하고 대상 타입의 범위를 검사하십시오. 부호 변환과 MISRA 필수 타입 범주 검사는 각각 별도 규칙도 제공합니다.",
      "badExample": "int32_t adc_raw = read_adc_channel(0);\nint16_t pwm_duty = adc_raw; // 위험: 32비트 -> 16비트 암묵적 축소 대입",
      "goodExample": "int32_t adc_raw = read_adc_channel(0);\nif (adc_raw >= INT16_MIN && adc_raw <= INT16_MAX) {\n    int16_t pwm_duty = (int16_t)adc_raw; // 범위 확인 후 명시적 캐스팅\n}"
    }
  ]
}
```

- **식별자**: `id`는 도구 ID 또는 앱의 자체 ID이며, 등록 규칙 간 중복 없이 사용한다. 엔진의 현재 구현 범위는 [아키텍처 문서](02_architecture.md#4-c-misra-검사기)를 따른다.
- **설명**: `title`, `summary`, `description`은 한글로 제공한다. 외부 엔진의 원문 진단과 표준 ID는 그대로 함께 표시할 수 있다. 파일은 프로그램 초기화 시 읽으며 실행 중 자동 재로드하지 않는다.
- **심각도와 정책**: `severity`는 앱의 기본 Error/Warning/Style 분류다. MISRA ID의 `obligation`은 해당 규칙의 의무 수준이고, 나머지 ID의 값은 프로젝트 검토 정책으로 표시한다. 저장된 `Critical` 정책은 UI에서 `우선 검토`로 표현하며 규칙의 공식 의무 등급으로 취급하지 않는다. `ObligationDisplay`, `SeverityDisplay` 같은 표시용 속성은 JSON에 저장하지 않는다. DB 미등록 상태를 뜻하는 `IsUnregistered`도 런타임 전용으로 JSON에 저장하지 않는다.
- **기본값**: `defaultEnabled`는 초기 활성화 여부다. 규칙 창의 필수/우선 검토 선택은 저장된 정책 값을 기준으로 동작하며, 심각도 열로 자동 대체하지 않는다.
- **코드 예시**: `badExample`/`goodExample`은 비워 둘 수 있다. 현재 296개 규칙은 양쪽 예시가 없으며 룰셋 창과 결함 상세에 미등록 안내를 표시한다. 예시는 문맥·타겟·규칙별 조건을 검토해야 한다.
- **미등록 ID**: runner는 개별 한글 해설 미등록 안내를 가진 임시 규칙을 생성한다. 원문은 각 진단의 메시지에 보존하고 정책은 미분류로 둔다. UI는 발견한 ID를 런타임 `AllRules`에 기본 ON으로 추가한 뒤 일반 필터를 적용한다. 진단이 미등록이라는 이유만으로 숨기지 않으며 룰셋에서 ON/OFF할 수 있다. 런타임 규칙은 DB에 자동 기록하지 않는다.
- **Clang-Tidy 별칭**: 하나의 진단에 여러 ID가 붙으면 활성 등록 ID를 우선 사용한다. 등록 별칭이 전부 OFF이면 보고하지 않는다. 축소 변환은 `implicitNarrowing`, 실수 루프 카운터는 `cert-flp30-c`로 통합 표시하는 경로도 있다.

---

## 3. 프로젝트 파일 포맷 (`*.jcproj`)

사용자가 구성한 프로젝트 환경과 룰셋 선택 상태를 JSON 형식의 `*.jcproj` 파일로 저장한다. 분석 결과·노트·검색어·규칙별 요약 및 접기 상태는 저장하지 않으며 불러온 뒤 분석을 다시 실행해야 한다. (구현: `Models/ProjectFileModel.cs`, `Services/DataRepository.cs`)

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
| `enabledRuleIds` | 저장 시점에 활성화(ON)된 규칙 ID 목록. 발견한 미등록 런타임 ID도 포함 |
| `disabledRuleIds` | 저장 시점에 비활성화(OFF)된 규칙 ID 목록. 불러올 때 두 목록 중 어디에도 없는 규칙(저장 이후 `rules_ko.json`에 새로 추가된 규칙)은 규칙 DB의 `defaultEnabled` 값을 따른다 |
| `suppressedIssues` | 개별 예외(무시) 처리 항목. `ruleId` + `filePath` + `line` 조합으로 결함을 식별하며, `message`는 예외 처리 당시의 검출 메시지(표시용) |

- **폴더 트리 복원 방식**: 불러오기 시 `targetPaths`/`nonTargetPaths`의 각 루트 폴더를 디스크에서 다시 스캔하여 트리를 구성하고(`.c`, `.h`, `.cpp`, `.hpp`만 포함), `excluded*Paths`에 기록된 하위 항목은 제외한다. 대상 폴더의 일부를 비대상으로 옮긴 경우 해당 경로는 `excludedTargetPaths`와 `nonTargetPaths`에 함께 기록된다.
- **경로 저장 원칙**: `.jcproj` 파일이 위치한 폴더 기준의 상대 경로를 우선 저장하여 프로젝트 폴더 이동 시에도 호환성을 유지하며, 다른 드라이브 등 상대 경로 변환이 불가능한 경우 절대 경로를 사용한다. (대상·참조·제외·예외 경로에 동일 적용)
- **미등록 선택 복원**: 저장된 ON/OFF ID가 DB에 없으면 공통 해설의 임시 규칙을 만든 뒤 선택 상태를 복원한다. 별도의 번역 텍스트나 원문 메시지 카탈로그를 저장하는 것은 아니다. 이후 재분석에서도 OFF 상태를 적용한다.
- **하위 호환**: `excludedTargetPaths`, `excludedNonTargetPaths`가 없는 이전 버전 파일도 그대로 불러올 수 있다 (제외 항목 없음으로 처리). `disabledRuleIds`가 없는 이전 버전 파일은 `enabledRuleIds`에 있는 규칙만 켜고 나머지는 모두 끈 상태로 불러온다.

## 4. 결과와 규칙별 요약의 런타임 모델

- `AnalysisIssue`: 규칙 ID, 심각도·정책, 메시지, 파일·줄·열, 한글 규칙 정보 참조를 갖는다. 중복 제거 키는 규칙 + 정규화 파일 + 줄 + 열이다.
- `SuppressedIssueEntry`: 예외 키는 규칙 + 정규화 파일 + 줄이다. 열이 없으므로 같은 줄의 동일 규칙 진단을 함께 제외한다. `message`는 저장 당시 표시용이며 예외 식별 키에 포함되지 않는다.
- `RuleIssueGroup`: 규칙 ID·한글 제목·발생 항목 목록과 접기 상태를 가진다. Count는 진단 항목 수이며 그룹은 건수 내림차순, 동률이면 ID 순으로 생성된다.
- `RuleIssueOccurrence`: 원본 `AnalysisIssue` 참조와 현재 파일에서 읽은 코드 한 줄을 갖는다. 위치는 파일·줄·열을 사용하며 원본 참조로 상세 설명과 코드 이동을 연결한다.
- 이들 요약 모델은 `RefreshFilteredDefects`가 만든 현재 표시 집합에서 파생되며 별도 JSON 형식이나 `.jcproj` 필드로 저장되지 않는다.
