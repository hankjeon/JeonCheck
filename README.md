# 전검사 (JeonCheck)

전검사는 임베디드 C 소스를 검사하는 **Windows 데스크탑 애플리케이션(.NET 10 WPF)**입니다. 내장 Cppcheck, C# AST 분석기, 선택적으로 실행하는 Clang-Tidy의 결과를 한글 설명과 함께 표시합니다.

크로스 컴파일러 없이 타겟 프리셋으로 분석을 시작할 수 있습니다. 실제 헤더·매크로·컴파일 옵션이 부족하면 결과가 제한될 수 있으며, MISRA 전체 규칙 지원이나 결함의 완전한 검출을 보장하지 않습니다.

## 주요 기능

- 폴더·파일을 검사 대상과 비대상/참조 트리로 나누고, 선택 항목을 이동·제거합니다. 폴더 스캔은 `.c`, `.h`, `.cpp`, `.hpp`를 포함하지만 주된 분석 대상은 C이며 Clang-Tidy에는 `-xc`를 전달합니다.
- C89/C99/C11/C17, 사용자 매크로, Cppcheck `--force`, Clang-Tidy 사용 여부를 설정합니다.
- TI DSP C6746/C6655/C6672/C6678 EABI, C6746/C6678 COFF, NXP T1024 32/64비트, PolarFire RV64, Versal AP/RP의 **11개 프리셋**을 제공합니다.
- Cppcheck `.dump`를 C#에서 해석하여 축소·부호 변환과 MISRA 일부 규칙을 검사합니다. Python addon을 실행하지 않습니다. 직접 보고하는 MISRA 규칙은 25개이며, 9.1은 Cppcheck 결과에서 별도로 매핑합니다. 정확한 목록과 제한은 [아키텍처 문서](doc/02_architecture.md)를 참고하십시오.
- 코드 줄에 오류·경고·분석 토큰 존재·미검사·예외 상태를 표시합니다. 같은 줄의 여러 진단은 `[규칙ID +N]` 배지와 전체 툴팁으로 확인합니다.
- 결과는 **결함 목록 / 규칙별 요약 / 노트 / 예외 항목** 탭으로 나눕니다. 규칙별 요약은 발생 건수 순으로 규칙을 묶고, 펼치면 발생 위치와 현재 파일의 코드 한 줄을 표시합니다. 항목 선택 시 코드와 상세 설명으로 이동합니다.
- 검색·분류 필터와 예외 처리 상태는 결함 목록과 규칙별 요약에 함께 반영됩니다.
- 특정 규칙의 파일·줄 단위 예외, 줄 전체 예외, 복원, 규칙 전체 OFF를 지원합니다. 개별 예외 키에는 열 번호가 없으므로 같은 줄의 같은 규칙은 함께 예외 처리됩니다.
- 타겟·옵션·트리 구성·룰셋·예외 목록을 `*.jcproj`로 저장합니다. 분석 결과와 요약 화면 상태는 저장하지 않습니다.
- 샘플 로드, 분석 취소, VS Code/메모장 열기, 탐색기 열기, 프로그램 정보 창을 제공합니다.

## 한글 설명과 분석 제한

`data/rules_ko.json`에는 371개 규칙의 제목·요약·상세 설명이 등록되어 있습니다. 코드 예시는 선택 항목이며, 양쪽 예시가 없는 296개 규칙은 미등록 안내를 표시합니다. 심각도와 MISRA 의무 등급, 그 외 규칙의 프로젝트 검토 정책을 구분해 표시합니다.

누락 헤더는 노트로 안내합니다. Clang-Tidy는 일부 누락 헤더를 임시 빈 파일로 보완하지만 타입·매크로의 의미를 복원하지 못하며, 컴파일 오류나 결과 누락이 남을 수 있습니다. TI DSP의 Clang-Tidy 분석에는 ARM 대체 트리플을 사용하므로 실제 DSP ABI와 일치하지 않습니다. 특히 COFF의 40비트 `long` 모델은 Clang-Tidy에 동일하게 적용되지 않습니다.

DB에 없는 ID도 원문과 개별 한글 해설 미등록 안내를 표시하며, 발견한 ID를 기본 ON으로 런타임 룰셋에 추가합니다. 결함 목록·규칙별 요약·상세에 연결되고 검색·분류·예외 상태를 적용합니다. 룰셋의 ON/OFF 선택은 프로젝트에 저장·복원하지만 실행 중 DB 파일을 자동 수정하거나 새 진단을 자동 번역하지는 않습니다.

## 개발·실행·검증

Windows 10/11 x64와 .NET 10 SDK에서 저장소 루트를 기준으로 실행합니다. GUI 실행에는 .NET Desktop Runtime이 필요하며 Windows용 SDK 설치에도 포함됩니다.

```sh
dotnet restore dotnetapp/JeonCheck.sln
dotnet build dotnetapp/JeonCheck.sln --no-restore -m:1
dotnet run --project dotnetapp/JeonCheck/JeonCheck.csproj
```

앱은 상위 경로에서 `data`와 `tools` 폴더를 찾으므로 저장소 구조를 유지하십시오. 빌드된 실행 파일만 다른 폴더로 복사하는 배포 방식은 구성되어 있지 않습니다. Cppcheck dump 생성에는 분석 소스 옆에 쓰기 권한도 필요합니다.

Linux에서는 Windows 대상 **교차 빌드와 콘솔 회귀 테스트**를 실행할 수 있습니다. WPF GUI와 내장 Windows 분석 도구는 실행 대상으로 검증되지 않았습니다.

```sh
dotnet restore dotnetapp/JeonCheck.sln -p:EnableWindowsTargeting=true
dotnet build dotnetapp/JeonCheck.sln --no-restore -p:EnableWindowsTargeting=true -m:1
dotnet run --project dotnetapp/JeonCheck.Analysis.Tests
```

클라우드에 SDK를 `/workspace/.local/dotnet`으로 설치한 경우 셸에서 `DOTNET_ROOT`와 `PATH`를 활성화해야 합니다. 회귀 테스트는 외부 테스트 패키지가 없는 콘솔 실행기이며, `dotnet test` 대신 위 `dotnet run` 명령을 사용합니다. 실패 시 종료 코드 1을 반환합니다. 검사 범위와 Windows 수동 검증 절차는 [테스트 안내](dotnetapp/JeonCheck.Analysis.Tests/README.md)를 참고하십시오.

## 폴더 및 문서

| 경로 | 내용 |
| --- | --- |
| `dotnetapp/` | WPF 앱과 솔루션 |
| `data/` | 타겟 프리셋·플랫폼 XML·한글 규칙 DB |
| `tools/` | Windows용 Cppcheck·Clang-Tidy와 분석 보조 파일 |
| `samples/` | 의도적인 위반을 포함한 C 샘플과 프로젝트 |
| `dotnetapp/JeonCheck.Analysis.Tests/` | 분석기·설명 처리·규칙별 요약 회귀 테스트 |
| [AGENTS.md](AGENTS.md) | 에이전트 행동 원칙과 구현 범위 |
| [요구사항](doc/01_requirements.md) | 기능과 남은 구현 차이 |
| [아키텍처](doc/02_architecture.md) | 분석·표시 파이프라인 |
| [데이터 명세](doc/03_data_spec.md) | 타겟·규칙·프로젝트 데이터 |
| [UI 설계](doc/04_ui_design.md) | 화면 조작과 결과 표시 |
