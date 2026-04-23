# Release Notes - v1.0.0

## Added
- `K-Explorer.exe` 배포 파일명 적용
- Release 실행 시 로그 콘솔창 비노출 처리
- 경로 히스토리 초기화 시 Back/Forward 스택 복원 로직 개선
- GitHub README/스크린샷/배포 안내 문서 정비

## Fixed
- 경로 히스토리 드롭다운은 보이지만 뒤로/앞으로가 기대대로 동작하지 않던 시나리오 보정

## Build
- Target: `net8.0-windows`
- Runtime: `win-x64`
- Publish: framework-dependent (`--self-contained false`)
