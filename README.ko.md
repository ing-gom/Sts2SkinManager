# Sts2SkinManager

설치된 **캐릭터 스킨 모드**, **카드 스킨 모드** (카드 초상화/아트), 그리고 **혼합 모드** (캐릭터 스파인 + 카드 아트가 한 묶음) 를 캐릭터 선택 화면의 단일 패널에서 함께 관리하는 Slay the Spire 2 모드. 전체 UI 는 접을 수 있는 **스킨 매니저** 토글 안에 들어 있어 평소에는 화면을 차지하지 않음.

🇺🇸 [English README](README.md) · 📦 [Nexus Mods 페이지](https://www.nexusmods.com/slaythespire2/mods/866)

![캐릭터 선택 화면 — 스킨 드롭다운, 카드 스킨 패널, 재시작 모달](docs/screenshots/character-select.png)

## 기능

- **자동 인식** — `<sts2>/mods/` 하위를 **재귀적으로** 스캔해 세 종류 모드를 자동 감지 (예: `mods/캐릭터/`, `mods/아트워크/`, `mods/유틸/` 처럼 카테고리 폴더로 정리해도 인식. `.pck` 파일명이 다르면 어떤 깊이든 OK — 같은 이름의 `.pck` 가 둘 이상 있으면 첫 번째만 유지하고 경고 로그). **DLL 동반 모드** 가 카테고리 폴더 안에 있으면 부팅 시 `mods/<modName>/` 에 디렉터리 junction 을 자동 생성해 게임 프레임워크가 manifest 와 DLL 을 다시 찾을 수 있게 해줘요 — 첫 부팅에만 재시작 한 번 필요:
  - **캐릭터 스킨 모드** — `.pck` 안에 `res://animations/characters/{캐릭터}/...` 경로 포함
  - **카드 스킨 모드** — `.pck` 가 `card_art/...` 를 덮거나 `card_portraits/` 를 포함
  - **혼합 모드** — 캐릭터 스파인과 카드 아트/이벤트 씬을 한 `.pck` 에 묶은 모드 (예: AncientWaifus)
- 접을 수 있는 **스킨 매니저** 토글 안에 모든 UI 가 있고, 기본 상태는 닫힘. 저장/되돌리기 버튼은 토글 옆에 항상 노출:
  - **캐릭터 스킨 드롭다운** — 캐릭터별 활성 variant 선택. 혼합 모드는 `📦` 표시 + 항목별 툴팁
  - **카드 스킨 탭** — 체크박스 토글, 우선순위 재정렬 (상단이 이김), 드래그앤드롭 또는 ↑/↓ 화살표
  - **혼합 모드 탭** — 혼합 모드를 dropdown 선택과 독립적으로 토글. 다른 메인 스파인 위에 혼합 모드의 카드/이벤트 추가요소만 덧입히고 싶을 때 사용 (스파인 충돌 시 dropdown 선택이 항상 이김)
- **호버 미리보기** — 적용 전 스킨 모습을 확인:
  - 캐릭터 스킨 → 드롭다운 옆 👁 아이콘 위에 호버
  - 카드 스킨 → 카드 row 의 라벨 위에 호버
  - 이미지 소스: `.pck` 옆 `preview.png` 가 있으면 우선, 없으면 `.pck` 에서 자동 추출 (캐릭터는 character-select 아트, 카드는 첫 카드 아트). 라이브 spine swap 안 함.
- **통합 저장 / 되돌리기** — 두 패널이 같은 Save 버튼을 공유. 모든 변경을 모은 뒤 한 번의 Save → 한 번의 재시작.
- **Steam 자동 재실행** — 확인 시 Steam 으로 STS2 재실행 (~5-10초). 취소 시 변경은 다음 부팅까지 대기 (Discard 로 완전 되돌리기).
- **오버레이 위치 설정** — 캐릭터 선택 화면 오버레이 위치를 좌상단/우상단 중 선택. 기본값은 **우상단** (게임의 멀티플레이 로비 패널이나 좌상단 기본 UI 와의 충돌 회피용). [Nexus ModConfig](https://www.nexusmods.com/slaythespire2/mods/27) 설치 시 인게임 dropdown 으로 좌상단(v0.8 레이아웃)으로 즉시 복귀 가능 — 재시작 불필요.
- **다국어 UI** — 게임 현재 언어를 따라감. 16개 언어 지원, 미지원 언어는 English 폴백.

## 동작 원리

`ProjectSettings.LoadResourcePack` Harmony patch 가 모드 부팅을 가로챔. 매니저가 `skin_choices.json` 을 읽어 캐릭터 actor instantiate 전에 variant 를 mount 하고, 카드 스킨 활성/순서 상태를 STS2 `settings.save` 에 기록.

`ModManager.TryLoadMod` 에 두 번째 Harmony patch — 활성화하지 않은 캐릭터 모드의 **DLL** 자체를 차단함. 이게 없으면 `Booba-Necrobinder-Mod` 같은 모드가 Harmony patch 로 자기가 선택되지 않았어도 모든 해당 캐릭터에 scale/position/skeleton 강제 변경을 적용함. 차단 목록은 매 부팅마다 `skin_choices.json` 기준으로 재계산 — 선택된 variant 와 enabled mixed mod 만 DLL 살아남음.

선택을 변경하면 `skin_choices.json` 갱신 + 10초 카운트다운 모달 표시. 확인 시 자동 재시작, 취소 시 변경은 대기 상태 유지. Discard 는 부팅 시점 상태로 모든 것을 복원.

## 설치

1. 최신 release zip 다운로드.
2. `Sts2SkinManager` 폴더를 `<Slay the Spire 2 설치 경로>/mods/` 에 복사.
3. 첫 부팅은 self-bootstrap (mod load order 재정렬) 1회 발생 → 10초 카운트다운 모달이 자동으로 Steam 재시작을 권유함 → 확인 누르면 적용. 이 재시작을 안 하면 SkinManager 보다 먼저 로드된 캐릭터 mod 의 강제 변경(scale 등) 이 이번 세션엔 남아있음.

## 사용법

STS2 실행 → 캐릭터 선택 화면.

- **우상단** (기본값, [ModConfig](https://www.nexusmods.com/slaythespire2/mods/27) 로 좌상단 전환 가능): `Skin [<캐릭터>]:` 드롭다운. 캐릭터 클릭 → variant 선택.
- **드롭다운 반대편** (우상단 모드면 좌측, 좌상단 모드면 우측): 👁 호버 아이콘 (현재 적용된 variant 와 다른 선택일 때만 + 미리보기 자원 있을 때만 표시) → 호버하면 character-select 아트 표시.
- **드롭다운 아래**: `카드 스킨 (N/M)` 패널. 체크박스, 순서 번호, ↑/↓, 드래그 핸들. 토글하거나 화살표/드래그앤드롭으로 순서 변경. row 의 라벨 위에 호버하면 그 모드의 첫 카드 아트 미리보기.
- 원하는 변경을 모두 한 뒤 **Save** 클릭 → 재시작 모달 → **지금 재시작** 또는 **나중에 재시작** (변경은 다음 부팅까지 대기).
- **Discard** 는 모든 변경을 부팅 시점 상태로 복원.

### 미리보기 이미지 만들기 (선택, 모드 작성자용)

`.pck` 옆에 `preview.png` (또는 `.jpg`, `.jpeg`, `.webp`) 를 두면 Sts2SkinManager 가 자동으로 사용. 없어도 `.pck` 에서 적절한 기본값을 자동 추출 (캐릭터 모드 → character-select PNG, 카드 모드 → 첫 카드 아트).

`skin_choices.json` 위치: `<user_data>/SlayTheSpire2/Sts2SkinManager/`. 파일 직접 편집 시 file watcher 가 감지해 동일 모달 표시.

### 모드팩 공유 (modpack preset)

Save 할 때마다 Sts2SkinManager 가 `<sts2>/mods/Sts2SkinManager/modpack_preset.json` 에도 동일한 내용을 자동으로 mirror 합니다. 친구에게 본인 모드팩을 통째로 보내려면:

1. 게임 내에서 원하는 조합으로 설정하고 `Save` 클릭.
2. `<sts2>/mods/` 폴더를 통째로 zip (또는 공유하고 싶은 스킨/카드 모드들 + `Sts2SkinManager` 폴더).
3. zip 전송. 받는 친구는 자기 `<sts2>/mods/` 에 압축 해제. 첫 실행 시 Sts2SkinManager 가 `modpack_preset.json` 으로부터 `skin_choices.json` 을 자동 seed — 드롭다운 선택, 카드 스킨 순서, 토글까지 그대로 적용됩니다.

받는 측 PC 에 preset 이 가리키는 모드가 없으면 그 항목만 조용히 `default` (기본 게임 아트) 로 떨어지고 나머지는 정상 적용.

> **Nexus 배포자 주의:** 릴리즈 zip 에 `modpack_preset.json` 을 **포함하면 안 됨**. 포함될 경우 사용자가 업데이트를 설치하는 순간 본인이 가진 preset 이 덮어쓰여집니다. 표준 릴리즈 zip (DLL + manifest + README + LICENSE) 은 이미 preset 을 포함하지 않습니다.

## 제약 사항

- **재시작 필요.** STS2 의 캐릭터 spine actor 가 runtime 데이터 교체 미지원 → Steam 을 통한 자동 재시작이 현실적 해법.
- 캐릭터 감지는 `res://animations/characters/{캐릭터}/...` 경로 기반. 카드 감지는 `card_art/...` 또는 `/card_portraits/` 기반. 아이콘만 덮는 모드는 미감지.
- 첫 설치 시 추가 재시작 1회 필요 (load-order self-bootstrap).
- 암호화된 `.pck` 는 미지원.

## 라이선스

MIT.
