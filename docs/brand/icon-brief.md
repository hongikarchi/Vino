# RhinoVibe 아이콘 — 제작 브리프 & 생성 프롬프트

**제품명** RhinoVibe · **태그라인** Vibe modeling for Rhino & Grasshopper · **회사** Archivibe

---

## 0. 먼저 읽을 것 — 실측으로 확인된 제약

세 시안을 실제 크기로 래스터라이즈해 검수한 결과가 이 브리프의 근거입니다.

- **아이콘이 실제로 소비되는 크기는 16·24px에 몰려 있습니다.** GH 리본(24), Rhino 툴바(24), 파비콘(16), 작업표시줄(32). 64px(Package Manager)은 첫인상 한 번뿐입니다.
- **16px에서 살아남는 요소 예산은 최대 2~3개.** 요소가 넷이면 반드시 뭉갭니다.
- **최소 획 굵기는 타일 폭의 8% 이상.** 16px 타일에서 8%는 1.3px입니다. 이보다 얇으면 회색 얼룩이 됩니다.
- **작은 장식(스파크, 소켓 점 여러 개)은 16px에서 소멸합니다.** 포인트 요소는 하나만, 그리고 크게.
- **의도치 않은 형상 오독을 반드시 확인하세요.** 실측에서 나온 실제 사례: 점 + 아래로 처진 곡선 → 웃는 얼굴, 캡슐 + 사선 꼬리 → 손잡이 달린 프라이팬.

---

## 1. 이미지 생성 모델용 프롬프트

> ⚠️ 생성 모델은 **정확한 벡터를 못 만듭니다.** 결과물은 시안·발상용으로 쓰고, 확정 후 Figma/Illustrator에서 벡터로 다시 그리세요. 곡선 대칭·모서리 반경·획 굵기는 손으로 잡아야 합니다.

### Base prompt (아래 `[CONCEPT]` 자리에 §2의 시드 하나를 넣으세요)

```
App icon for "RhinoVibe", an AI copilot plugin for Rhino 3D and Grasshopper.
Flat vector-style icon on a single rounded-square tile, 1:1 square, centered.

SUBJECT: [CONCEPT]

COLOR: the tile is a deep ultramarine blue with a subtle diagonal gradient,
#4F60F5 at the top-left corner to #222C86 at the bottom-right corner.
The mark itself is pure white, completely flat, no gradient inside the mark.
Exactly ONE small accent element in vermilion #FF5A3C. No other colors anywhere.

COMPOSITION: corner radius about 23% of the tile width. The mark is centered
with even margins of about 15% on every side. The gesture rises from the
lower-left toward the upper-right.

HARD CONSTRAINTS: maximum 3 distinct shapes in the entire mark. Every stroke
is at least 8% of the tile width thick. No hairlines, no fine detail, no small
scattered ornaments, no text or lettering except where the concept specifies a
single letterform. No drop shadow, no bevel, no 3D, no glow, no glossy highlight.

STYLE: crisp geometric flat icon in the visual register of a professional
CAD and engineering tool. Confident and structural, not a playful consumer AI app.
Must stay legible when scaled down to 16x16 pixels.
```

### Negative prompt

```
purple to blue AI gradient, neon glow, scattered sparkles, chrome, metallic,
3D render, bevel, emboss, glossy reflection, drop shadow, photorealistic,
rhinoceros animal illustration, realistic rhino head, grasshopper insect,
grass green, cluttered, many small elements, thin hairlines, hatching,
text, letters, watermark, signature, gradient mesh, isometric, perspective,
sticker outline, white border, busy background, noise texture
```

---

## 2. 컨셉 시드 — `[CONCEPT]` 자리에 넣을 세 가지

**A · 와이어 혼** — 큰 크기에서 가장 아름답고, 16px에서 가장 손해를 봅니다. 보조 그래픽(웹 히어로·배너) 후보.
```
a single thick tapered stroke that begins as a solid round node at the lower
left and sweeps upward to the right, narrowing into a sharp pointed tip.
It reads simultaneously as a cable and as a rhinoceros horn. The round node
at the origin is the one vermilion accent.
```

**B · V-혼 모노그램** — 16px 실측에서 유일하게 통과. 이름(Vibe)과 직접 연결됩니다.
```
a bold uppercase letter V. Its left arm is a straight thick stroke; its right
arm curves upward and narrows into a sharp horn-like point, so the letter
leans forward. A solid vermilion dot sits on top of the left arm like a
connection port where a cable would plug in.
```

**C · AI 컴포넌트** — 24px 이상에서 설명력이 가장 좋지만 16px에서 무너집니다.
```
a rounded rectangular capsule shaped like a node in a visual programming
graph, with a bold four-pointed star knocked out of its center, and one short
thick cable leaving the capsule toward the upper right, ending in a solid
vermilion dot.
```

> 실측 권고는 **B를 앱 아이콘으로, A를 큰 자리의 보조 그래픽으로** 쓰는 하이브리드입니다. 물론 새 방향을 만드셔도 됩니다 — §0의 제약만 지키면 됩니다.

---

## 3. 모노 글리프 (Rhino 8 툴바용) — 별도 프롬프트

툴바에는 타일이 없습니다. 형태만으로 버텨야 하는 가장 가혹한 자리라 별도로 그립니다.

```
The same mark as a single-color silhouette with no tile and no background,
solid #14151C on transparent. All elements merge into one continuous solid
shape; the vermilion accent becomes part of the same solid silhouette rather
than a separate color. Strokes 20% thicker than the tile version to survive
at 24 pixels. No outline, no container, no background shape.
```

화이트 버전(#FFFFFF)도 같은 형태로 하나 더 — Rhino 다크 테마용입니다.

---

## 4. 납품 파일 목록

| 파일 | 용도 |
|---|---|
| `rhinovibe.svg` | 마스터 벡터 (타일 포함, 256 그리드 기준) |
| `rhinovibe-small.svg` | **16·24px 전용** — 획 2배, 여백 축소, 장식 제거 |
| `rhinovibe-mono-ink.svg` / `-mono-white.svg` | Rhino 툴바 (라이트/다크) |
| `rhinovibe-{16,24,32,48,64,128,256,512}.png` | 32px 이상은 마스터에서, **16·24는 small 버전에서** 생성 |
| `rhinovibe-wordmark.svg` | 선택 — 마크 + "RhinoVibe" 로고타입 |

### 코드가 실제로 소비하는 건 세 개뿐

나머지는 여유분입니다. 이 셋은 파일명이 빌드에 하드코딩돼 있으니 교체 시 참조도 같이 고쳐야 합니다.

| PNG | 쓰이는 곳 | 참조 위치 |
|---|---|---|
| `vino-48.png` | **Rhino 패널 탭 아이콘** — 사용자가 하루 종일 보는 자리. Rhino가 탭 스트립 크기(≈16~24px)로 축소해 표시하므로 **작은 크기 가독성이 여기서 결정됩니다.** | `src/Vino.Rhino/Vino.Rhino.csproj:15` (EmbeddedResource → `Vino.Rhino.PanelIcon.png`) |
| `vino-256.png` | **Yak 패키지 `icon.png`** — Package Manager 목록, 첫인상 | `scripts/build-package.ps1:284` |
| `vino-24.png` | Grasshopper **로드된 라이브러리 목록** 아이콘 (`GH_AssemblyInfo.Icon`). 자주 보이지 않습니다. | `src/Vino.Grasshopper/Vino.Grasshopper.csproj:16` (EmbeddedResource → `Vino.Grasshopper.AssemblyIcon.png`) |

> **정정** — 앞서 "GH 리본 탭"이라고 했는데 틀렸습니다. 이 플러그인은 `GH_Component`를 하나도 등록하지 않습니다(`VinoAssemblyInfo.cs`에 `GH_AssemblyInfo` + `GH_AssemblyPriority`만 존재). **Grasshopper 리본에 탭이 생기지 않습니다.** 24px는 GH 라이브러리 목록용이라 비중이 낮습니다.
>
> 부수 효과: 등록된 GH 컴포넌트가 없다는 건 **보존해야 할 컴포넌트 GUID도 없다**는 뜻이라 리네임 위험이 그만큼 줄어듭니다.

→ 우선순위는 **48px(패널 탭) → 256px(스토어) → 24px** 순입니다. 48px가 작게 축소돼 표시되므로 §0의 16px 규칙이 그대로 적용됩니다.

- Yak 매니페스트가 요구하는 건 `icon.png` **64×64** (PNG 또는 JPEG). 512는 Food4Rhino 앱 로고용 여유분.
- 커밋 위치는 `assets/icons/`. **이미 같은 자리에 자리표시자 세트가 있습니다** — `vino-{16,24,32,48,64,128,256}.png`, 전부 초록 배경에 검은 "G". 새 파일로 교체하고 `scripts/build-package.ps1:284`의 `assets\icons\vino-256.png` 참조만 새 이름으로 바꾸면 됩니다. 기존 크기 구성이 위 표와 일치하므로 사실상 1:1 교체입니다.
- **광학 사이징이 핵심**: 16·24px PNG를 마스터에서 그냥 축소하면 안 됩니다. small 버전에서 뽑으세요.

---

## 5. 납품 전 자가 검수 5분

1. 16×16 PNG로 실제 출력한 뒤 **10배 이상 확대**해 화소를 직접 보세요. 브라우저에서 CSS로 줄인 벡터는 실제보다 관대하게 보입니다.
2. 그 16px 이미지를 **처음 보는 사람에게 보여주고 "뭐로 보여요?"** 물어보세요. 의도한 것과 다른 답이 나오면 형태를 고쳐야 합니다.
3. 흑백으로 변환해도 형태가 남는지 확인 (색에 의존하면 안 됨).
4. 흰 배경과 검은 배경 양쪽에 올려보세요.
5. Rhino 툴바·GH 리본은 24px 기준입니다 — 그 크기에서 이웃 아이콘들과 무게가 맞는지 보세요.

만드신 뒤 PNG를 주시면 실제 UI 맥락(GH 리본·Rhino 툴바·Package Manager·파비콘)에 얹어서 검수 페이지를 다시 만들어 드리겠습니다.
