# K-Explorer

Windows용 업무형 파일 탐색기입니다. 빠른 키보드 중심 탐색 경험에, 즐겨찾기/핀/메모/검색/2패널·4패널 전환 기능을 결합했습니다.

## 주요 기능
- 2패널/4패널 모드 전환
- 탭 기반 탐색 + 뒤로/앞으로 히스토리 (탭 우클릭 메뉴, 모든 탭 닫기)
- 즐겨찾기 폴더/파일, 핀 고정
- 메모 목록/자주가는폴더/자주사용한파일 가상 경로
- 빠른 검색 및 결과에서 포함 폴더 열기
- 복사/이동/삭제/이름변경/F키 단축키 중심 작업
- Windows 탐색기에서 파일 끌어놓기, 클립보드 붙여넣기
- 툴바 명령 프롬프트 버튼 (현재 폴더에서 열기, 시작 명령 지정)
- 주요 명령 단축키 변경 (환경설정)
- Shift + 방향키 다중 범위 선택 지원
- 파일 복사/이동/삭제 시 "모두 취소" 기능 지원
- 숨김/시스템 파일·폴더 표시 여부 설정, `속성` 열 표시 on/off
- 프로그램 전체 글꼴 설정 (Pretendard 내장, 설치 불필요)
- 이미지 뷰어: 파일목록 사이드바, 썸네일 현재 위치 표시, 창에 맞춤/원본 크기, 이미지 크기 조정·스탬프
- 패널 간 키보드 포커스 간섭(Drift) 차단

## 스크린샷
> 아래 이미지는 최신 UI로 계속 갱신됩니다.

| 화면 1 | 화면 2 |
| --- | --- |
| ![Screen 1](docs/1.png) | ![Screen 2](docs/2.png) |
| ![Screen 3](docs/3.png) | ![Screen 4](docs/4.png) |
| ![Screen 5](docs/5.png) |  |

## 다운로드
- 최신 릴리즈: `https://github.com/wookoon2024/K-Explorer/releases/latest`
- 최신 버전: `v1.3.21`
- 최신 버전 링크: `https://github.com/wookoon2024/K-Explorer/releases/tag/v1.3.21`
- 최신 파일 다운로드 (zip): `https://raw.githubusercontent.com/wookoon2024/K-Explorer/main/K-Explorer-win-x64-v1.3.21.zip`
- [빠른다운로드 (zip)](https://raw.githubusercontent.com/wookoon2024/K-Explorer/main/K-Explorer-win-x64-v1.3.21.zip)
- 배포 파일: `K-Explorer-win-x64-v1.3.21.zip`
- 최종 배포일: `2026-09-23`

## 버전 히스토리
- [v1.3.21 릴리즈 노트](RELEASE_NOTES_v1.3.21.md)
- [v1.3.20 릴리즈 노트](RELEASE_NOTES_v1.3.20.md)
- [v1.3.19 릴리즈 노트](RELEASE_NOTES_v1.3.19.md)
- [v1.3.18 릴리즈 노트](RELEASE_NOTES_v1.3.18.md)
- [v1.3.17 릴리즈 노트](RELEASE_NOTES_v1.3.17.md)
- [v1.3.16 릴리즈 노트](RELEASE_NOTES_v1.3.16.md)

이전 버전의 릴리즈 노트는 저장소 커밋 기록에서 확인할 수 있습니다.

## 실행 방법
1. 위 [빠른다운로드](#다운로드)에서 `K-Explorer-win-x64-v1.3.21.zip` 다운로드
2. 압축 해제
3. `K-Explorer.exe` 실행

> .NET 8 데스크톱 런타임이 설치돼 있어야 합니다.

## 개발 환경
- .NET 8
- WPF (MVVM)
- C#

## 빌드
```powershell
dotnet build WorkFileExplorer.sln -c Release
```

## 퍼블리시
```powershell
dotnet publish WorkFileExplorer.App\WorkFileExplorer.App.csproj -c Release -r win-x64 --self-contained false -o artifacts\publish\WorkFileExplorer
Compress-Archive -Path artifacts\publish\WorkFileExplorer\* -DestinationPath K-Explorer-win-x64-<버전>.zip -Force
```

## 라이선스
개인 프로젝트로, 별도의 라이선스를 정하지 않았습니다. 모든 권리는 저작자에게 있습니다.

번들된 글꼴 Pretendard는 SIL Open Font License 1.1에 따라 포함되어 있으며, 라이선스 원문은 `WorkFileExplorer.App\Assets\Fonts\LICENSE-Pretendard.txt`(배포 zip에서는 `Assets\Fonts\LICENSE-Pretendard.txt`)에 있습니다.
