# 전검사 (JeonCheck)

**전검사(JeonCheck)**는 고신뢰성이 요구되는 임베디드 C 소스 코드의 정적 분석을 쉽고 직관적으로 수행할 수 있도록 돕는 **Windows 데스크탑 애플리케이션(.NET 10 WPF)**입니다.

복잡한 컴파일러 빌드 환경 없이도 타겟 프로세서 프리셋과 내장된 **Cppcheck**, **Clang-Tidy (LLVM)** 및 **C# 네이티브 AST/MISRA 분석 엔진**을 결합하여 결함 검출, 암묵적 축소 변환 검사, CERT-C/MISRA 규칙 검증, 코드 라인별 분석 범위 시각화를 제공합니다.

---

## 주요 기능

- **컴파일러 비종속 간편 타겟 프리셋**
  - **TI DSP**: `TMS320C6746`, `C6655`, `C6672`, `C6678` (COFF 40-bit `long` / EABI 32-bit `long` 및 TI 전용 키워드 자동 처리)
  - **NXP**: `QorIQ T1024` (PowerPC e5500, Big Endian)
  - **Microchip**: `PolarFire SoC` (64-bit RISC-V, Little Endian)
  - **AMD/Xilinx**: `Versal AP` (64-bit ARM Cortex-A72), `Versal RP` (32-bit ARM Cortex-R5F)
- **유연한 소스 관리 및 분리 분석**
  - 폴더 및 파일 단위 소스 코드 임포트
  - 실제 정적 분석을 수행할 **검사 대상 코드**와 단순 참조/제외할 **비대상 코드** 분리 설정
  - 누락된 포함 파일(`#include`) 등으로 인해 분석하지 못한 항목은 에러가 아닌 **Info / Note**로 분리 표시
- **라인별 검사 상태 시각화 코드 뷰어**
  - 🟢 **검사 완료 줄**: 전처리 및 구문 분석이 정상 수행된 코드 라인
  - ⚪ **미검사(Skipped) 줄**: `#ifdef` 조건 불충족, 매크로 미정의 등으로 분석에서 제외된 라인
  - 🔴 **결함 검출 줄**: 에러, 경고, 타입 변환 위험 및 MISRA 규칙 위반이 발생한 라인 하이라이트
- **C# 네이티브 MISRA C 2012 & Clang-Tidy (CERT-C) 듀얼 엔진**
  - Python 의존성 없이 Cppcheck `--dump` AST를 C#에서 직접 분석
  - **Clang-Tidy 포터블 내장 (`tools/clang-tidy/`)**: 누락된 `#include` 헤더가 있어도 자동 임시 더미(Stub) 헤더를 생성하여 중단 없이 `bugprone-*`, `cert-*`(CERT-C), `clang-analyzer-*` 경로 추적 분석 수행
  - **암묵적 축소 변환(`Implicit Narrowing Conversion`)** 및 부호 변환 정밀 검출
  - **내장 한글 규칙 해설 DB (`rules_ko.json`)**: MISRA C 2012, CERT-C, Clang-Tidy 및 Cppcheck 에러에 대한 상세 한글 설명과 위반/개선 코드 예시 제공
- **맞춤형 룰셋(Ruleset) 선택, 코드 줄 단위 예외/복원 및 프로젝트 관리**
  - 규칙별 한글 요약 확인 및 개별 ON/OFF 선택 창 제공
  - 코드 뷰어의 각 줄(Line)에서 바로 **`[🔇 이 줄 예외]`** 처리(보라색 음영 표시) 및 **`[↩ 다시 검사(복원)]`** 원클릭 토글 지원
  - 임포트한 폴더/파일 목록, 대상/비대상 설정, 타겟 프리셋, 선택한 룰셋을 프로젝트 파일(`*.jcproj`)로 저장 및 불러오기

---

## 폴더 구조

```text
JeonCheck/
├── AGENTS.md           # AI 에이전트 작업 지침 및 개발 원칙
├── README.md           # 프로젝트 개요 문서
├── doc/                # 요구사항, 아키텍처, 설계 및 데이터 명세 문서
├── tools/              # 외부 도구 (Cppcheck 포터블 내장 폴더)
├── data/               # 타겟 프리셋(.xml/.json) 및 한글 규칙 해설 DB(rules_ko.json)
├── samples/            # 검증 및 테스트용 임베디드 C 샘플 코드
└── src/                # .NET 10 WPF 솔루션(JeonCheck.sln) 및 애플리케이션 소스 코드
```

---

## 개발 및 실행 환경

- **OS**: Windows 10 / 11 (x64)
- **Framework**: .NET 10.0 (WPF)
- **정적 분석 코어**: Cppcheck Portable (`tools/cppcheck/`) + Clang-Tidy Portable (`tools/clang-tidy/`) + JeonCheck Native AST Analyzer (C#)
