# 분석기 및 결과 표시 회귀 테스트

## 자동 검사

저장소 루트에서 .NET 10 SDK로 실행합니다.

```sh
dotnet run --project dotnetapp/JeonCheck.Analysis.Tests
```

외부 테스트 패키지나 WPF 런타임이 필요 없는 콘솔 실행기입니다. `dotnet test` 대신 위 명령을 사용하며 성공/실패 수를 출력하고 실패 시 종료 코드 1을 반환합니다. 테스트 프로젝트는 솔루션에 포함되지만 **솔루션 빌드만으로 테스트가 실행되지는 않습니다**.

현재 검사 범위는 다음과 같습니다.

- 합성 AST를 실제 분석기에 전달하여 캐스트 이후 변환, 부호 비교, 포인터 변환, switch, 복합 대입, 시프트, 입출력 함수 및 재귀의 검출·비검출 사례 확인
- dump XML 파서부터 분석기까지의 일부 연결 경로
- 한글 설명 DB의 중복 ID·필수 필드, 예시 미등록 안내, MISRA 의무/프로젝트 정책 표시
- runner의 헤더 누락 노트 및 미등록 진단의 XML/텍스트 파싱 → ViewModel 필터 → 목록·요약·상세 모델 연결
- 발견된 규칙의 기본 ON, 검색·분류·예외/복원·OFF, 프로젝트 선택 저장·재로드·재분석 및 발생 위치별 원문·심각도 보존
- 규칙별 집계·정렬·코드 미리보기와 검색·분류·OFF·예외/복원·프로젝트 초기화 연동
- 같은 줄의 여러 진단에서 원본 상세 대상 보존 및 존재하는 그룹의 접기 상태 유지

합성 AST·XML은 실제 Windows 도구가 모든 C 소스에서 같은 데이터를 생성한다는 보장이 아닙니다. Windows용 Cppcheck/Clang-Tidy 실행, GUI 조작, Python addon 전체와의 동등성 검증은 자동 검사에 포함하지 않습니다.

## 빌드

Windows에서는 다음을 사용합니다.

```sh
dotnet restore dotnetapp/JeonCheck.sln
dotnet build dotnetapp/JeonCheck.sln --no-restore -m:1
```

Linux 교차 빌드는 다음을 사용합니다.

```sh
dotnet restore dotnetapp/JeonCheck.sln -p:EnableWindowsTargeting=true
dotnet build dotnetapp/JeonCheck.sln --no-restore -p:EnableWindowsTargeting=true -m:1
```

이 클라우드 환경에서 로컬 SDK를 사용하려면 먼저 활성화합니다.

```sh
export DOTNET_ROOT=/workspace/.local/dotnet
export PATH="$DOTNET_ROOT:$PATH"
export NUGET_PACKAGES=/workspace/.cache/nuget/packages
```

## Windows 수동 검증 절차

다음은 실행해야 할 절차이며, Linux에서 통과한 결과로 간주하지 않습니다.

1. 저장소 구조와 Windows용 내장 도구를 유지한 상태에서 `dotnet run --project dotnetapp/JeonCheck/JeonCheck.csproj`를 실행합니다.
2. 샘플 프로젝트와 타겟·C 표준·엔진 옵션을 확인하고 분석합니다. 노트와 비정상 종료 여부를 함께 확인합니다. 샘플은 의도적인 결함을 포함하므로 결함 0건을 성공 기준으로 삼지 않습니다.
3. 결과에 네 탭이 표시되고, 규칙별 요약의 총 진단 수가 동일 필터의 결함 목록 건수와 맞는지 확인합니다. 그룹 펼치기와 파일·줄·코드 표시를 확인합니다.
4. 같은 줄의 서로 다른 진단을 각각 선택해 코드 줄 이동과 오른쪽 상세 설명을 확인합니다. 줄 배지의 `+N`과 전체 툴팁도 확인합니다.
5. 두 탭의 검색어·분류 공유, 건수 정렬, 예외 처리·복원과 규칙 OFF 반영을 확인합니다. 같은 파일·줄·규칙의 서로 다른 열은 기존 정책에 따라 함께 예외 처리되어야 합니다.
6. 프로젝트를 저장·불러와 트리·옵션·룰셋·예외가 복원되고, 분석 결과와 요약은 다시 분석해야 생성되는지 확인합니다.
7. 분석을 취소하고 실행 프로세스 종료와 dump 정리를 확인합니다. 소스 옆 기존 dump가 있었다면 복원 여부도 확인합니다.
8. 외부에서 소스 파일을 삭제한 뒤 발생 위치를 선택하여 누락 안내가 표시되고 다른 파일의 같은 줄로 잘못 이동하지 않는지 확인합니다.
