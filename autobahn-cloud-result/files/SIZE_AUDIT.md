# Аудит размера сборки (задание G)

> Только отчёт. В `Assets/`, `ProjectSettings/` и коде ничего не менялось.
> Снимок: ветка `main`, коммит `05c86a4`. Цель сборки из `Editor/BuildGame.cs`: **StandaloneOSX**, `BuildOptions.None` (без LZ4).

## 0. Коротко

- В сборку попадает всё, что лежит в `Assets/Drive/Resources` (Unity берёт в сборку каждый файл из `Resources`, даже неиспользуемый), плюс единственная сцена `Drive.unity` и настройки URP. Сцена почти пустая: весь мир строится кодом, поэтому **весь контент сборки = Resources**.
- Исходники в Resources: **1092.1 МБ**. Оценка того, сколько это займёт в сборке: **≈ 814.2 МБ** (в основном текстуры ≈ 513.0 МБ, модели ≈ 200 МБ, аудио ≈ 40.9 МБ).
- **Точно безопасно** (убрать из Resources то, что код не грузит, и понизить частоту дискретизации звуков): **≈ 35.0 МБ**.
- **Проверить руками** (снизить разрешение и сжатие текстур, убрать ассеты, нужные только QA, пережать радио, включить сжатие мешей): ещё **≈ 212–242 МБ**. Это оценка, а не замер.
- Отдельно: 305.2 МБ исходников в `Assets` **в сборку не попадают** (`Parked`, демо-паки). Их удаление уменьшит только репозиторий, сборка от этого не изменится.
- `isReadable` на моделях и текстурах влияет на **оперативную память, а не на размер сборки**. Почти все модели код читает сам (`mesh.vertices`, `CombineMeshes`), так что выключать его нельзя (§7).

### Сводная таблица экономии

| # | Шаг | Класс | ≈ Экономия в сборке, МБ | Точность оценки |
|---:|---|---|---:|---|
| S1 | Убрать `Env/Boulders/rockFree/LightingData.asset` из Resources | **точно безопасно** | 23.8 | точная (файл идёт в сборку как есть) |
| S2 | Убрать остальные неиспользуемые файлы из Resources (§5.1) | **точно безопасно** | 10.7 | ± 30 % (текстуры и модели оценены) |
| S3 | Звуки с частотой 96 кГц → Override 44100 Гц | **точно безопасно** | 0.6 | ± 50 %, объём маленький |
| M1 | Текстуры окружения 2048 → 1024 и HQ (BC7) → Normal quality (DXT1) для цветных карт без альфы (§6.2–6.3) | проверить руками | 112.8 | ± 20 % (расчёт по формату, без учёта LZ4) |
| M2 | Текстуры машин и персонажа 2048 → 1024 (салоны M300 и Mercedes 560, Blake) | проверить руками | 47.7 | ± 20 % |
| M3 | Здания, которые грузит только `DriveValidation` (QA): вынести из Resources или сменить путь в QA | проверить руками | 14.6 | ± 30 % (модели) |
| M4 | Радио: Vorbis quality 0.55 → 0.40 | проверить руками | 6.5 | ± 50 % |
| M5 | `meshCompression` Off → Medium на больших моделях (§7) | проверить руками | ≈ 30–60 | **очень приблизительно**: реальный объём мешей из pointer-файла не узнать |
| M6 | `BuildGame.cs`: `BuildOptions.CompressWithLz4HC` | проверить руками | ≈ 15–30 % от всей сборки | приблизительно; это сжатие всего архива, а не ассетов |

Рекомендуемый порядок: **S1 → S2 → S3 → замер → M1 → M2 → замер → M3 → M4 → M5 → замер → M6 (по желанию)**. Цифры отдельных шагов нельзя просто сложить: M1 и M2 затрагивают разные текстуры, но внутри M1 понижение разрешения и смена формата относятся к одним и тем же файлам, и это уже учтено в сумме.

## 1. Как считалось

**Источник размеров.** Большие файлы (`png/jpg/tga/tif/psd/exr/fbx/obj/dae/glb/gltf/bin/wav/mp3` и **все `*.asset`**) лежат в Git LFS. Размер и `oid sha256` взяты из pointer-файлов. Чтобы узнать разрешение текстур и длительность звуков, через LFS batch API скачивались только **первые байты** объектов (HTTP Range): 2 КБ у PNG, до 256 КБ у JPEG, 64 КБ у WAV, плюс хвост с IFD у 23 файлов TIFF. Полностью скачаны только небольшие YAML-файлы `*.asset` (всего 37 штук, ≈ 0,3 МБ). Без них граф не построить: `ProjectSettings/EditorBuildSettings.asset` тоже лежит в LFS. Одно исключение: `Env/Boulders/rockFree/LightingData.asset` (23,8 МБ) не скачивался, его ссылки не разбирались.

**Корни графа.**
1. Сцены из `EditorBuildSettings` с `enabled: 1`: только `Assets/Drive/Scenes/Drive.unity`. Она ссылается лишь на `Boot.cs`.
2. `GraphicsSettings` и `QualitySettings`: `PC_RPAsset`, `PC_Renderer`, `UniversalRenderPipelineGlobalSettings`, `DefaultVolumeProfile`.
3. Всё содержимое `Assets/Drive/Resources` (1300 файлов). Внутри Resources файл считается **используемым**, если его путь совпадает с путём из `Resources.Load*` в коде (190 прямых совпадений) либо до него можно дойти по GUID от такого файла. Пути собраны вручную из кода: литералы и динамические имена (`Bodywork.Of`, `Airplane.Spec`, `Foliage.Use`, `GridCity.NycNames`, `Art.RoadSign`, `Island`, `Weapons.Defs`, `HumanRig.ClipNames`, `DriveAudio`, `Art.Textured(...)`), а также `LoadAll("Radio")`. Шейдеры из `Resources/Shaders` считаются используемыми: код берёт их через `Shader.Find`.
4. `Editor/BuildSlim.cs`: 24 корня машин (все они есть и в `Bodywork.Of`) и `KeepFolders` для glTF-машин. Папки glTF-машин считаются используемыми целиком: glTFast читает `.bin` и текстуры рядом с `.gltf`.

**Рёбра графа.** Все `guid:` из `.unity/.prefab/.mat/.asset/.controller/.anim/.shadergraph/.lighting` и из `.meta` (кроме собственного guid файла).

**Неявные ссылки (важно).** 29 моделей импортируются с `materialLocation: 0` (устаревший поиск внешних материалов по имени), ещё 43 — со встроенными материалами. Такая связь «модель → материал или текстура» хранится в `Library`, а не в `.meta`, поэтому по GUID её не видно. Все `.mat` и текстуры в паке используемой модели помечены как **«из кода (неявно)»** (§5.3). Здесь анализ сознательно консервативен. Косвенное подтверждение: `BuildSlim` уже убрал из `Resources/Cars` всё, чего не видел `AssetDatabase.GetDependencies`, а эти файлы остались на месте.

**Оценка размера в сборке** (≠ размер исходника):
- Текстуры: итоговое разрешение = исходное, делённое пополам, пока не станет ≤ `maxTextureSize` (с учётом override для Standalone). Байт на пиксель: DXT1 0,5; DXT5, BC5 и BC7 — 1; без сжатия 3–4. Мип-уровни добавляют ×4/3. Crunch не используется нигде. Возможная ошибка — ±20 %: Unity может выбрать другой формат для PNG с «пустой» альфой; NPOT-текстуры; LZ4 не учтён.
- Аудио: PCM-объём (длительность × частота × каналы × 2 байта) × коэффициент Vorbis: ≈ 0,06 + 0,16 × quality. Это эмпирика, ±50 %.
- Модели: FBX, DAE, GLB и BIN — ≈ 0,9 × исходника, OBJ — ≈ 0,35 × исходника. **Грубо.** Реальные числа вершин и формат вершинных данных без скачивания моделей не узнать.
- Остальное (`.prefab/.mat/.anim/.asset`) — как исходник.

**Сверка путей `Resources.Load*` с файлами:**

| Путь в `Resources.Load*` | Где в коде | Результат |
|---|---|---|
| `QaFlags` | QaFlags | **файла нет** |
| `Env/Trees/Textures/Ash/Leaf_3` | Foliage.PackMaterial | **файла нет** |
| `Env/Trees/Textures/Spruce/Leaf_3` | Foliage.PackMaterial | **файла нет** |
| `Cars/MercedesE50/scene` | Bodywork.Of | несколько файлов с этим путём: `Drive/Resources/Cars/MercedesE50/scene.bin`, `Drive/Resources/Cars/MercedesE50/scene.gltf` |
| `Cars/AudiS4B8/scene` | Bodywork.Of | несколько файлов с этим путём: `Drive/Resources/Cars/AudiS4B8/scene.bin`, `Drive/Resources/Cars/AudiS4B8/scene.gltf` |

- `QaFlags`: файл `Resources/QaFlags.txt` создаётся только для QA-сборок, код обрабатывает `null`. Это нормально.
- `Leaf_3` у видов Ash и Spruce: код сам переходит на `Leaf_1` (`?? PackMaterial(folder + "/Leaf_1")`). Ничего не сломано, но это лишний вызов `Resources.Load` при каждом дереве нового вида.
- `scene.gltf` и `scene.bin` дают одинаковый путь, но `Load<GameObject>` берёт `.gltf` по типу. Проблемы нет.

## 2. Где лежит объём

| Категория | Файлов | Исходники, МБ | ≈ в сборке, МБ |
|---|---:|---:|---:|
| сцена/настройки | 5 | 0.1 | 0.1 |
| из кода | 948 | 908.1 | 667.2 |
| из кода (неявно) | 216 | 132.7 | 97.8 |
| только QA | 73 | 16.1 | 14.6 |
| в Resources, не используется | 63 | 35.2 | 34.5 |
| вне сборки | 194 | 305.2 | 168.9 |
| скрипт | 95 | 1.6 | 0.0 |
| **Итого попадает в сборку** | **1305** | **1092.1** | **814.2** |

«Итого попадает в сборку» включает и неиспользуемые файлы из Resources: Unity всё равно их пакует.

## 3. Топ-50 самых тяжёлых файлов (размер LFS-объекта)

| # | Файл | Размер, МБ | Статус | Что это / импорт |
|---:|---|---:|---|---|
| 1 | `SportCar/Textures/Floor.tga` | 67.11 | вне сборки | 4096×4096, max 2048, ≈5.59 МБ в сборке |
| 2 | `Drive/Resources/Cars/LabCoupe/LabCoupe.fbx` | 24.51 | из кода | модель, isReadable=1, meshCompression=0 |
| 3 | `Drive/Resources/Env/Boulders/rockFree/LightingData.asset` | 23.82 | в Resources, не используется | asset |
| 4 | `Drive/Resources/Cars/G63/G63.fbx` | 23.63 | из кода | модель, isReadable=1, meshCompression=0 |
| 5 | `Drive/Resources/Planes/A340/a340.glb` | 20.58 | из кода | модель, isReadable=None, meshCompression=None |
| 6 | `Drive/Resources/Cars/Military/Texture/Military_combat_Jeep_BaseMap.png` | 19.48 | из кода (неявно) | 4096×4096, max 1024, ≈0.70 МБ в сборке |
| 7 | `Drive/Parked/Cars/SportCar/Models/Wheels/Ring/Textures/Ring_2/Ring_2_Base_Color.tga` | 16.78 | вне сборки | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 8 | `Drive/Parked/Cars/SportCar/Models/Wheels/Ring/Textures/Ring_3/Ring_3_Base_Color.tga` | 16.78 | вне сборки | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 9 | `Drive/Parked/Cars/SportCar/Models/Wheels/Ring/Textures/Ring_4/Ring_4_Base_Color.tga` | 16.78 | вне сборки | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 10 | `Drive/Parked/Cars/SportCar/Models/Wheels/Ring/Textures/Ring_5/Ring_5_Base_Color.tga` | 16.78 | вне сборки | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 11 | `Drive/Parked/Cars/SportCar/Models/Wheels/Ring/Textures/Ring_6/Ring_6_Base_Color.tga` | 16.78 | вне сборки | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 12 | `Drive/Resources/Cars/SportCar/Wheels/Ring/Textures/Ring_1_Base_Color.tga` | 16.78 | из кода | 2048×2048, max 1024, ≈1.40 МБ в сборке |
| 13 | `Drive/Resources/Cars/MercedesE50/scene.bin` | 16.53 | из кода | glTF-данные |
| 14 | `Drive/Parked/Cars/Military/Texture/Military_combat_Jeep MaskMap.png` | 14.92 | вне сборки | 4096×4096, max 2048, ≈5.59 МБ в сборке |
| 15 | `Drive/Resources/Planes/Piper/PiperMeridian.fbx` | 14.69 | из кода | модель, isReadable=1, meshCompression=0 |
| 16 | `Drive/Resources/Cars/Porsche911/porsche911.fbx` | 14.35 | из кода | модель, isReadable=1, meshCompression=0 |
| 17 | `1950s Classic Car #6 Variant/Built-In/Models/50sClassicCar#6/1950sClassicCar#6.dae` | 12.93 | вне сборки | модель, isReadable=0, meshCompression=0 |
| 18 | `1950s Classic Car #6 Variant/HDRP/Models/50sClassicCar#6/1950sClassicCar#6.dae` | 12.93 | вне сборки | модель, isReadable=0, meshCompression=0 |
| 19 | `Drive/Resources/Cars/Classic50/Models/50sClassicCar#6/1950sClassicCar#6.dae` | 12.93 | из кода | модель, isReadable=1, meshCompression=0 |
| 20 | `Car and transportation sounds collection/TramIdleLoop.wav` | 12.61 | вне сборки | 71 с, loadType 0, ≈2.77 МБ |
| 21 | `Drive/Parked/Cars/TruckLow/Textures/Others/Road Normals.png` | 12.37 | вне сборки | 2048×2048, max 8192, ≈5.59 МБ в сборке |
| 22 | `Drive/Resources/Cars/TruckLevo/Meshes/SK_Truck_Levo_M1035.fbx` | 11.90 | из кода | модель, isReadable=1, meshCompression=0 |
| 23 | `Drive/Resources/Textures/Terrain/terrain_dirt_normal.png` | 11.07 | из кода | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 24 | `Drive/Resources/Textures/Terrain/terrain_sand_normal.png` | 10.48 | из кода | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 25 | `Drive/Resources/Cars/Military/Texture/Military_combat_Jeep Normal.png` | 10.39 | из кода (неявно) | 4096×4096, max 1024, ≈1.40 МБ в сборке |
| 26 | `Drive/Resources/Textures/Terrain/terrain_rock_normal.png` | 10.34 | из кода | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 27 | `Drive/Resources/Radio/Track7_505.mp3` | 10.19 | из кода | 255 с, loadType 2, ≈6.65 МБ |
| 28 | `Drive/Resources/Cars/TruckLevo/Textures/T_M1035-KAPLA_BUNA SONRA BAK_Normal.png` | 10.10 | из кода | 2048×2048, max 512, ≈0.35 МБ в сборке |
| 29 | `Drive/Resources/Radio/Track4_Brodyaga.mp3` | 9.82 | из кода | 246 с, loadType 2, ≈6.41 МБ |
| 30 | `Drive/Parked/Cars/TruckLevo/Meshes/SK_Truck_Levo_M1590.fbx` | 9.76 | вне сборки | модель, isReadable=0, meshCompression=0 |
| 31 | `Drive/Resources/Textures/Terrain/terrain_grass_normal.png` | 9.36 | из кода | 2048×2048, max 2048, ≈5.59 МБ в сборке |
| 32 | `Drive/Resources/Cars/SportCar/Models/SportCar_1/SportCar_1.FBX` | 9.28 | из кода | модель, isReadable=1, meshCompression=0 |
| 33 | `Drive/Resources/Cars/BMWX6/BMW_X6.fbx` | 7.73 | из кода | модель, isReadable=1, meshCompression=0 |
| 34 | `Drive/Resources/Cars/Touring/Textures/Chev/Chevy_Carbody666.tga` | 7.34 | из кода | 2048×2048, max 1024, ≈0.70 МБ в сборке |
| 35 | `Drive/Resources/Cars/Military/Texture/Military_combat_Jeep_AO.png` | 7.30 | из кода (неявно) | 4096×4096, max 1024, ≈0.70 МБ в сборке |
| 36 | `Drive/Resources/Cars/TruckLevo/Textures/T_M1035-KAPLA_Material _705_Normal.png` | 6.88 | из кода | 2048×2048, max 512, ≈0.35 МБ в сборке |
| 37 | `Drive/Resources/Env/Rocks/Materials/Rock/rock_set_mat_Normal.png` | 6.81 | из кода | 2048×2048, max 1024, ≈1.40 МБ в сборке |
| 38 | `Drive/Resources/Env/Buildings/Materials/Textures/TexturesCom_Plaster_Rough_1K_normal.tif` | 6.32 | из кода | 1024×1024, max 2048, ≈1.40 МБ в сборке |
| 39 | `Drive/Resources/Env/Buildings/Materials/Textures/TexturesCom_Brick_Modern_1K_normal.tif` | 6.29 | из кода | 1024×1024, max 2048, ≈1.40 МБ в сборке |
| 40 | `Drive/Resources/Env/Buildings/Materials/Textures/TexturesCom_Metal_CopperPolished_1K_normal.tif` | 6.29 | из кода | 1024×1024, max 2048, ≈1.40 МБ в сборке |
| 41 | `Drive/Resources/Env/Buildings/Materials/Textures/TexturesCom_Metal_SteelBrushed_1K_normal.tif` | 6.29 | из кода (неявно) | 1024×1024, max 2048, ≈1.40 МБ в сборке |
| 42 | `Drive/Resources/Env/Buildings/Materials/Textures/TexturesCom_Paint_Epoxy_1K_normal.tif` | 6.29 | из кода | 1024×1024, max 2048, ≈1.40 МБ в сборке |
| 43 | `Drive/Resources/Env/Buildings/Materials/Textures/TexturesCom_Plastic_Shiny_1K_normal.tif` | 6.29 | из кода | 1024×1024, max 2048, ≈1.40 МБ в сборке |
| 44 | `Drive/Resources/Radio/Track1_GorillaZoe.mp3` | 6.15 | из кода | 256 с, loadType 2, ≈6.69 МБ |
| 45 | `Drive/Resources/Cars/TruckLevo/Textures/T_M1035-KAPLA_Material _720_Normal.png` | 6.06 | из кода | 2048×2048, max 512, ≈0.35 МБ в сборке |
| 46 | `Drive/Resources/Models/interior.obj` | 5.92 | из кода | модель, isReadable=1, meshCompression=0 |
| 47 | `Drive/Resources/Cars/TruckLevo/Textures/T_M1035-KAPLA_Material _700_Normal.png` | 5.79 | из кода | 2048×2048, max 512, ≈0.35 МБ в сборке |
| 48 | `Drive/Resources/Env/Rocks/Materials/Rock/rock_set_mat_AlbedoTransparency.png` | 5.79 | из кода | 2048×2048, max 1024, ≈0.70 МБ в сборке |
| 49 | `Drive/Resources/Cars/TruckLevo/Textures/T_M1035-KAPLA_Material _719_Normal.png` | 5.77 | из кода | 2048×2048, max 512, ≈0.35 МБ в сборке |
| 50 | `Drive/Resources/Cars/TruckLevo/Textures/T_M1035-KAPLA_Material _718_Normal.png` | 5.62 | из кода | 2048×2048, max 512, ≈0.35 МБ в сборке |

Заметно: самый тяжёлый файл, `SportCar/Textures/Floor.tga` (67 МБ, 4K TGA), **в сборку не попадает**: это демо-пак вне Resources. Из тех, что попадают, больше всего весят модели машин и самолётов (LabCoupe, G63, A340, Piper, Porsche) и 23,8 МБ неиспользуемого `LightingData.asset`.

## 4. Дубликаты по LFS oid

| Размер копии, МБ | Копий | В сборке копий | Экономия в сборке, МБ | Экономия в репозитории, МБ | Файлы |
|---:|---:|---:|---:|---:|---|
| 12.93 | 3 | 1 | 0.00 | 25.86 | `1950s Classic Car #6 Variant/Built-In/Models/50sClassicCar#6/1950sClassicCar#6.dae` (вне сборки)<br>`1950s Classic Car #6 Variant/HDRP/Models/50sClassicCar#6/1950sClassicCar#6.dae` (вне сборки)<br>`Drive/Resources/Cars/Classic50/Models/50sClassicCar#6/1950sClassicCar#6.dae` (из кода) |
| 3.36 | 2 | 1 | 0.00 | 3.36 | `Drive/Parked/Cars/Classic50/Textures/50sClassicCar#6/bodymap backup/bodymap_TEXTURE.png` (вне сборки)<br>`Drive/Resources/Cars/Classic50/Textures/50sClassicCar#6/bodymap_TEXTURE.png` (из кода) |
| 1.28 | 2 | 1 | 0.00 | 1.28 | `Car and transportation sounds collection/2CV6MotorNoStart2.wav` (вне сборки)<br>`Drive/Resources/Sounds/2CV6MotorNoStart2.wav` (из кода) |
| 0.37 | 2 | 1 | 0.00 | 0.37 | `Car and transportation sounds collection/2CV6MotorNoStart.wav` (вне сборки)<br>`Drive/Resources/Sounds/2CV6MotorNoStart.wav` (из кода) |
| 0.36 | 2 | 1 | 0.00 | 0.36 | `Car and transportation sounds collection/2CV6KeysOut.wav` (вне сборки)<br>`Drive/Resources/Sounds/2CV6KeysOut.wav` (из кода) |
| 0.21 | 2 | 2 | 0.17 | 0.21 | `Drive/Resources/Cars/AudiS4B8/textures/Chrome_baseColor.png` (из кода)<br>`Drive/Resources/Cars/AudiS4B8/textures/grid_baseColor.png` (из кода) |
| 0.17 | 2 | 2 | 0.17 | 0.17 | `Drive/Resources/Cars/AudiS4B8/textures/glows_baseColor.png` (из кода)<br>`Drive/Resources/Cars/AudiS4B8/textures/glows_emissive.png` (из кода) |
| 0.12 | 2 | 1 | 0.00 | 0.12 | `Car and transportation sounds collection/ElevatorButton.wav` (вне сборки)<br>`Drive/Resources/Sounds/ElevatorButton.wav` (из кода) |
| 0.02 | 6 | 6 | 1.75 | 0.11 | `Drive/Resources/Env/StreetLights/Textures/Street Lighting 2/Black/Street.Lighting.2_Black.Street.Lighting.2_Metallic.png` (из кода)<br>`Drive/Resources/Env/StreetLights/Textures/Street Lighting 2/Grey/Street.Lighting.2_Grey.Street.Lighting.2_Metallic.png` (из кода)<br>`Drive/Resources/Env/StreetLights/Textures/Street Lighting 2/White/Street.Lighting.2_White.Street.Lighting.2_Metallic.png` (из кода)<br>`Drive/Resources/Env/StreetLights/Textures/Street Lighting 4/Black/Street.Lighting,4_Black.Street.Lighting.4_Metallic.png` (из кода)<br>`Drive/Resources/Env/StreetLights/Textures/Street Lighting 4/Grey/Street.Lighting,4_Grey.Street.Lighting.4_Metallic.png` (из кода)<br>`Drive/Resources/Env/StreetLights/Textures/Street Lighting 4/White/Street.Lighting,4_White.Street.Lighting.4_Metallic.png` (из кода) |
| 0.09 | 2 | 2 | 0.17 | 0.09 | `Drive/Resources/Cars/AudiS4B8/textures/Chrome_metallicRoughness.png` (из кода)<br>`Drive/Resources/Cars/AudiS4B8/textures/interior_metallicRoughness.png` (из кода) |

Вывод: **в сборке дубликатов почти нет, экономия ≈ 2.3 МБ.** Это одинаковые маленькие текстуры внутри glTF-пака AudiS4B8 и одинаковые `_Metallic`-карты фонарей. Трогать glTF-текстуры не советую: их пути прописаны в `scene.gltf`. Карты фонарей можно свести к одной, переназначив материалы (≈ 1,75 МБ, проверить руками). Остальные копии — это Resources против демо-пака или `Parked`. Они экономят только репозиторий (≈ 31.9 МБ).

## 5. Неиспользуемые ассеты

### 5.1. В Resources, но код их не грузит и никто на них не ссылается (**идут в сборку зря**)

Это **единственная категория «неиспользуемых», удаление которой уменьшает сборку**. Рекомендация: не удалять, а перенести в `Assets/Drive/Parked/...`, как это делает `BuildSlim` (`AssetDatabase.MoveAsset` сохраняет GUID, перенос обратим).

| Файл | Исходник, МБ | ≈ в сборке, МБ | Почему считаем мёртвым |
|---|---:|---:|---|
| `Drive/Resources/Env/Boulders/rockFree/LightingData.asset` | 23.82 | 23.82 | LightingData демо-сцены пака; сцены из Resources в сборку не входят, а сам .asset — входит |
| `Drive/Resources/Weapons/LowPoly/Models/M2_50cal.fbx` | 1.73 | 1.55 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Env/Trees/Textures/Moss/Moss_Normal.png` | 3.30 | 1.40 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Textures/grass_height.png` | 0.75 | 1.40 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Textures/pine_height.png` | 0.81 | 1.40 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Characters/Blake/Animations/crashing.anim` | 0.49 | 0.49 | клип не входит в `HumanRig.ClipNames`, AnimatorController на него не ссылается |
| `Drive/Resources/Characters/Blake/Animations/jump start.anim` | 0.46 | 0.46 | клип не входит в `HumanRig.ClipNames`, AnimatorController на него не ссылается |
| `Drive/Resources/Characters/Blake/Animations/dead2.anim` | 0.40 | 0.40 | клип не входит в `HumanRig.ClipNames`, AnimatorController на него не ссылается |
| `Drive/Resources/Weapons/LowPoly/Models/M249.fbx` | 0.42 | 0.38 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Characters/Blake/Animations/jump landing.anim` | 0.35 | 0.35 | клип не входит в `HumanRig.ClipNames`, AnimatorController на него не ссылается |
| `Drive/Resources/Weapons/LowPoly/Models/SR_Scope_00.fbx` | 0.33 | 0.30 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Characters/Blake/Animations/jump descending.anim` | 0.29 | 0.29 | клип не входит в `HumanRig.ClipNames`, AnimatorController на него не ссылается |
| `Drive/Resources/Characters/Blake/Animations/fight pose.anim` | 0.24 | 0.24 | клип не входит в `HumanRig.ClipNames`, AnimatorController на него не ссылается |
| `Drive/Resources/Characters/Blake/Animations/jump ascending.anim` | 0.24 | 0.24 | клип не входит в `HumanRig.ClipNames`, AnimatorController на него не ссылается |
| `Drive/Resources/Env/StreetLights/Textures/Street Lighting 2/Black/Street.Lighting.2_Black.Street.Lighting.2_Height.png` | 0.01 | 0.17 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Env/StreetLights/Textures/Street Lighting 2/Grey/Street.Lighting.2_Grey.Street.Lighting.2_Height.png` | 0.01 | 0.17 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Env/StreetLights/Textures/Street Lighting 2/White/Street.Lighting.2_White.Street.Lighting.2_Height.png` | 0.01 | 0.17 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Env/StreetLights/Textures/Street Lighting 4/Black/Street.Lighting,4_Black.Street.Lighting.4_Height.png` | 0.01 | 0.17 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Env/StreetLights/Textures/Street Lighting 4/Grey/Street.Lighting,4_Grey.Street.Lighting.4_Height.png` | 0.01 | 0.17 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Env/StreetLights/Textures/Street Lighting 4/White/Street.Lighting,4_White.Street.Lighting.4_Height.png` | 0.01 | 0.17 | карта высот: `Island.Surface` грузит `_height` только для sand/rock; материалы на файл не ссылаются |
| `Drive/Resources/Weapons/LowPoly/Models/ANPEQ15.fbx` | 0.11 | 0.10 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Models/RPG7.fbx` | 0.06 | 0.06 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Models/ELCAN.fbx` | 0.06 | 0.05 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Models/Flash.fbx` | 0.06 | 0.05 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Models/TAN_LR_Scope_01.fbx` | 0.06 | 0.05 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Sounds/Foot/playerSound/walk_sound 1.wav` | 0.23 | 0.05 | из этой папки код грузит только `die-01` |
| `Drive/Resources/Weapons/LowPoly/Models/RGD-5.fbx` | 0.05 | 0.04 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Models/Smoke.fbx` | 0.04 | 0.04 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Env/Buildings/Sky and Fog Settings Profile.asset` | 0.03 | 0.03 | профиль HDRP-демо-сцены пака |
| `Drive/Resources/Sounds/Foot/playerSound/run_sound 1.wav` | 0.11 | 0.03 | из этой папки код грузит только `die-01` |
| `Drive/Resources/Sounds/Foot/playerSound/freakin_zombies.wav` | 0.22 | 0.02 | из этой папки код грузит только `die-01` |
| `Drive/Resources/Weapons/LowPoly/Prefabs/M2_50cal.prefab` | 0.02 | 0.02 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Env/Buildings/Sample/LightingData.asset` | 0.02 | 0.02 | LightingData демо-сцены пака; сцены из Resources в сборку не входят, а сам .asset — входит |
| `Drive/Resources/Sounds/Foot/playerSound/die_02.wav` | 0.13 | 0.01 | из этой папки код грузит только `die-01` |
| `Drive/Resources/Weapons/LowPoly/Prefabs/M249.prefab` | 0.01 | 0.01 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Sounds/Foot/playerSound/hit_sound.wav` | 0.11 | 0.01 | из этой папки код грузит только `die-01` |
| `Drive/Resources/Sounds/Foot/playerSound/jump.wav` | 0.07 | 0.01 | из этой папки код грузит только `die-01` |
| `Drive/Resources/Weapons/LowPoly/Prefabs/RGD-5.prefab` | 0.01 | 0.01 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Prefabs/RPG7.prefab` | 0.01 | 0.01 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Prefabs/Flash.prefab` | 0.01 | 0.01 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Prefabs/Smoke.prefab` | 0.01 | 0.01 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Weapons/LowPoly/Prefabs/TAN_LR_Scope_01.prefab` | 0.01 | 0.01 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Env/Signs/Prefabs/Maximum_Pole_Sign.prefab` | 0.01 | 0.01 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Weapons/LowPoly/Prefabs/SR_Scope_00.prefab` | 0.00 | 0.00 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Env/Signs/Prefabs/School_Bus_Stop_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Electric_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Fire_Exit_Symbol_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Toxic_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Warning_Electric_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Fire_Exit_Text_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Safety_Equipment_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Emergency_Exit_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Construction_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Weapons/LowPoly/Prefabs/ANPEQ15.prefab` | 0.00 | 0.00 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Env/Signs/Prefabs/Bridge_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Danger_Volt_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Weapons/LowPoly/Prefabs/ELCAN.prefab` | 0.00 | 0.00 | модель/префаб оружия, которого нет в `Weapons.Defs` (M1911, Uzi, AK74, M4_8, Bennelli_M4, M107) |
| `Drive/Resources/Env/Signs/Prefabs/Security_Sign_1.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Low_Bridge_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/No_Pass_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/12-6_Sign.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Env/Signs/Prefabs/Road_Sign_Pole.prefab` | 0.00 | 0.00 | знак не используется ни в одном `Art.RoadSign(...)` |
| `Drive/Resources/Materials/Recolor.mat` | 0.00 | 0.00 | шейдер Recolor берётся через `Shader.Find`, сам материал нигде не используется |
| **Итого** | **35.2** | **34.5** | |

«Точно безопасно»: `LightingData.asset` (S1), модели и префабы оружия, которых нет в `Weapons.Defs`, неиспользуемые префабы знаков, неиспользуемые карты `_height`, `Moss_Normal`, `Recolor.mat` и профиль Sky and Fog. Clips Blake (`crashing`, `jump *`, `dead2`, `fight pose`) и звуки `playerSound/*` в текущем коде не грузятся, но выглядят как задел на будущее: перед переносом лучше спросить владельца. Экономия там < 3 МБ.

### 5.2. Только для QA (`DriveValidation` → `Resources.LoadAll("Env/Buildings/Prefabs/Buildings")`)

Игра (`GridCity.NycNames`) использует 6 вариантов зданий: `building_1_*`, `building_2_*` и `building_6_*`. `DriveValidation` загружает **все** префабы папки, поэтому модели `building_3/4/5/7/8` и `tree.fbx` попадают в сборку только ради QA.

| Папка | Файлов | Исходник, МБ | ≈ в сборке, МБ |
|---|---:|---:|---:|
| `Drive/Resources/Env/Buildings/Models/` | 6 | 14.70 | 13.23 |
| `Drive/Resources/Env/Buildings/Prefabs/Parts/` | 17 | 0.98 | 0.98 |
| `Drive/Resources/Env/Buildings/Prefabs/Buildings/` | 49 | 0.42 | 0.42 |
| `Drive/Resources/Env/Buildings/Prefabs/` | 1 | 0.00 | 0.00 |
| **Итого** | **73** | **16.1** | **14.6** |

Проверить руками (M3): либо сузить QA-проверку до `NycNames`, либо перенести неиспользуемые префабы и модели в `Parked`. Экономия ≈ 14.6 МБ. Без правки `DriveValidation.cs` проверка будет считать меньше зданий. Сама она при этом не упадёт: проверяется наличие, а не количество.

### 5.3. «Из кода (неявно)»: связь найдена эвристикой, а не по GUID

| Пак | Файлов | Исходник, МБ | ≈ в сборке, МБ |
|---|---:|---:|---:|
| `Drive/Resources/Cars/Military/` | 6 | 37.89 | 3.50 |
| `Drive/Resources/Cars/Mercedes/` | 41 | 37.36 | 27.10 |
| `Drive/Resources/Cars/M300/` | 34 | 27.33 | 28.04 |
| `Drive/Resources/Env/Buildings/` | 13 | 15.75 | 6.84 |
| `Drive/Resources/Planes/Piper/` | 4 | 7.70 | 16.65 |
| `Drive/Resources/Ships/Carrier/` | 2 | 2.98 | 5.68 |
| `Drive/Resources/Weapons/AIM120/` | 4 | 1.62 | 6.29 |
| `Drive/Resources/Characters/Blake/` | 1 | 0.62 | 1.40 |
| `Drive/Resources/Planes/B1/` | 1 | 0.39 | 1.40 |
| `Drive/Resources/Cars/AudiR8/` | 17 | 0.35 | 0.36 |
| `Drive/Resources/Cars/Stylized/` | 4 | 0.28 | 0.01 |
| `Drive/Resources/Cars/TruckLevo/` | 28 | 0.11 | 0.11 |
| `Drive/Resources/Cars/LabCoupe/` | 21 | 0.08 | 0.08 |
| `Drive/Resources/Cars/Van/` | 2 | 0.08 | 0.18 |
| `Drive/Resources/Cars/Touring/` | 19 | 0.07 | 0.07 |
| `Drive/Resources/Cars/Tocus/` | 13 | 0.05 | 0.05 |
| `Drive/Resources/Cars/TruckLow/` | 6 | 0.02 | 0.02 |
| **Итого** | **216** | **132.7** | **97.8** |

Эти файлы, скорее всего, действительно используются: их не тронул `BuildSlim`, у которого был полный граф зависимостей из `AssetDatabase`. Если локальный агент захочет проверить, пусть откроет в Unity Editor `AssetDatabase.GetDependencies(root, true)` для корней из §1. Всё, что есть в этой таблице, но не попало в зависимости, кандидат в `Parked`.

### 5.4. Вне сборки (влияют только на размер репозитория и время импорта)

| Папка | Файлов | Исходник (LFS), МБ | Экономия в сборке |
|---|---:|---:|---|
| `Drive/Parked/Cars` | 97 | 167.09 | 0 (в сборку не попадает) |
| `SportCar` | 12 | 68.75 | 0 (в сборку не попадает) |
| `Car and transportation sounds collection` | 24 | 42.44 | 0 (в сборку не попадает) |
| `1950s Classic Car #6 Variant` | 57 | 26.88 | 0 (в сборку не попадает) |
| `Drive/Settings/UniversalRenderPipelineGlobalSettings.asset` | 1 | 0.03 | 0 (в сборку не попадает) |
| `Drive/Settings/DefaultVolumeProfile.asset` | 1 | 0.02 | 0 (в сборку не попадает) |
| `_Recovery` | 2 | 0.01 | 0 (в сборку не попадает) |
| **Итого** | **194** | **305.2** | **0** |

`Drive/Parked` — хранилище `BuildSlim`. Сам скрипт возвращает оттуда файлы, если машина снова начинает на них ссылаться, поэтому **не удалять**. Демо-паки (`SportCar`, `1950s Classic Car`, `Car and transportation sounds collection`) можно удалить ради размера репозитория и LFS-квоты, но **сборка от этого не станет меньше**.

## 6. Текстуры

В сборке 513 текстур, ≈ 513.0 МБ. Распределение по итоговой стороне:

| Итоговая сторона в сборке | Текстур | ≈ МБ |
|---:|---:|---:|
| 2048 | 41 | 181.05 |
| 2000 | 1 | 2.67 |
| 1306 | 1 | 2.27 |
| 1024 | 261 | 283.97 |
| 973 | 1 | 0.30 |
| 920 | 1 | 0.37 |
| 693 | 2 | 0.54 |
| 640 | 1 | 0.31 |
| 512 | 163 | 39.15 |
| 256 | 39 | 2.36 |
| 128 | 1 | 0.01 |
| 32 | 1 | 0.00 |

Проверено:
- **Итоговое разрешение ≥ 4096: нет.** Все 4K-исходники (Military, Piper `pm_b/pm_c`, `Floor.tga`) уже ограничены через `maxTextureSize` до 2048 или 1024. `BuildSlim` ограничивает машины: 1024, салон — 2048.
- **Текстур без сжатия (`textureCompression: 0`) в сборке: нет.**
- **`isReadable: 1` у текстур в сборке: нет.** Код не вызывает `GetPixel` или `ReadPixels` на загруженных текстурах, так что трогать нечего.
- Crunch не используется. Его можно включить как дополнительный шаг, но это заметно удлиняет импорт, поэтому в расчёт он не включён.

### 6.1. Итоговое разрешение ≥ 4096 → 2048

| Текстура | Исходник | Сейчас в сборке | Настройки импорта (Standalone) | Экономия, МБ |
|---|---|---:|---|---:|
| **Итого (0)** | | **0.0** | | **0.0** |

### 6.2. Окружение, самолёты, корабль, ракета: 2048 → 1024 (M1, проверить руками)

| Текстура | Исходник | Сейчас в сборке | Настройки импорта (Standalone) | Экономия, МБ |
|---|---|---:|---|---:|
| `Drive/Resources/Planes/Piper/pm_b.png` | 4096×4096 | 2048×2048, ≈5.59 МБ | max 2048, compression 1, alpha | 4.19 |
| `Drive/Resources/Planes/Piper/pm_c.png` | 4096×4096 | 2048×2048, ≈5.59 МБ | max 2048, compression 1, alpha | 4.19 |
| `Drive/Resources/Ships/Carrier/Type004.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 1, alpha | 4.19 |
| `Drive/Resources/Textures/Terrain/terrain_dirt_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/Terrain/terrain_dirt_normal.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2, normal map | 4.19 |
| `Drive/Resources/Textures/Terrain/terrain_grass_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/Terrain/terrain_grass_normal.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2, normal map | 4.19 |
| `Drive/Resources/Textures/Terrain/terrain_rock_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/Terrain/terrain_rock_normal.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2, normal map | 4.19 |
| `Drive/Resources/Textures/Terrain/terrain_sand_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/Terrain/terrain_sand_normal.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2, normal map | 4.19 |
| `Drive/Resources/Textures/asphalt_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/concrete_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/facade_brick_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/facade_limestone_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/facade_render_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/grass_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/rock_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/sand_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Textures/shoulder_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 4.19 |
| `Drive/Resources/Planes/Piper/Leather_003_NRM.jpg` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Weapons/AIM120/AIM120Texture.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| **Итого (22)** | | **117.4** | | **88.1** |

На что смотреть: террейн вблизи (`terrain_*` — их всего 8, и они тайлятся; при потере чёткости оставить 2048 только для `terrain_*_albedo`), фасады и асфальт на уровне камеры водителя, Piper (`pm_b/pm_c` — это атлас всего самолёта), палуба авианосца (`Type004.png`).

### 6.3. Сжатие HQ (BC7) на цветных текстурах без альфы → Normal quality (DXT1) (M1, проверить руками)

У фото-текстур в `Resources/Textures` стоит `textureCompression: 2` (High Quality). На Standalone это BC7, 1 байт на пиксель. Для RGB без альфы Normal quality даёт DXT1 — 0,5 байта на пиксель, то есть вдвое меньше. Вместе с §6.2 экономия по окружению составляет ≈ **112.8 МБ**, а не сумму двух таблиц.

| Текстура | Исходник | Сейчас в сборке | Настройки импорта (Standalone) | Экономия, МБ |
|---|---|---:|---|---:|
| `Drive/Resources/Textures/Terrain/terrain_dirt_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/Terrain/terrain_grass_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/Terrain/terrain_rock_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/Terrain/terrain_sand_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/asphalt_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/concrete_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/facade_brick_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/facade_limestone_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/facade_render_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/grass_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/rock_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/sand_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/shoulder_albedo.jpg` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 2 | 2.80 |
| `Drive/Resources/Textures/bark_albedo.jpg` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/bark_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/bark_height.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_brick_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_brick_emission.jpg` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_brick_height.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_limestone_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_limestone_emission.jpg` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_limestone_height.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_render_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_render_emission.jpg` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/facade_render_height.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/grass_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/leaf_birch_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/leaf_broadleaf_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/leaf_bush_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/leaf_maple_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/needle_pine_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/pine_albedo.jpg` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/pine_ao.png` | 1024×1024 | 1024×1024, ≈1.40 МБ | max 2048, compression 2 | 0.70 |
| `Drive/Resources/Textures/prop_container_albedo.jpg` | 512×512 | 512×512, ≈0.35 МБ | max 2048, compression 2 | 0.17 |
| `Drive/Resources/Textures/prop_curtain_albedo.jpg` | 512×512 | 512×512, ≈0.35 МБ | max 2048, compression 2 | 0.17 |
| `Drive/Resources/Textures/prop_paint_albedo.jpg` | 512×512 | 512×512, ≈0.35 МБ | max 2048, compression 2 | 0.17 |
| `Drive/Resources/Textures/prop_plaster_albedo.jpg` | 512×512 | 512×512, ≈0.35 МБ | max 2048, compression 2 | 0.17 |
| `Drive/Resources/Textures/prop_plastic_albedo.jpg` | 512×512 | 512×512, ≈0.35 МБ | max 2048, compression 2 | 0.17 |
| `Drive/Resources/Textures/prop_steel_albedo.jpg` | 512×512 | 512×512, ≈0.35 МБ | max 2048, compression 2 | 0.17 |
| `Drive/Resources/Textures/prop_wood_albedo.jpg` | 512×512 | 512×512, ≈0.35 МБ | max 2048, compression 2 | 0.17 |
| `Drive/Resources/Textures/asphalt_ao.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/asphalt_height.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/concrete_ao.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/concrete_height.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/rock_ao.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/rock_height.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/sand_ao.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/sand_height.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/shoulder_ao.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| `Drive/Resources/Textures/shoulder_height.png` | 256×256 | 256×256, ≈0.09 МБ | max 2048, compression 2 | 0.04 |
| **Итого (50)** | | **104.0** | | **52.0** |

AO- и height-карты (`*_ao`, `*_height`) одноканальные. Лучший вариант для них — `textureType: SingleChannel` (BC4, 0,5 байта на пиксель), экономия та же. Normal-карты с HQ на Standalone тоже дают BC5 или BC7 по 1 байту на пиксель, поэтому их здесь нет.

### 6.4. Машины и персонаж: 2048 → 1024 (M2, проверить руками)

`BuildSlim` намеренно оставляет салону 2048 (`CockpitWords`). Эти текстуры видны из кабины, поэтому только ручная проверка.

| Текстура | Исходник | Сейчас в сборке | Настройки импорта (Standalone) | Экономия, МБ |
|---|---|---:|---|---:|
| `Drive/Resources/Cars/M300/Textures/Details-0.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 1, alpha | 4.19 |
| `Drive/Resources/Cars/M300/Textures/Details-2.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 1, alpha | 4.19 |
| `Drive/Resources/Characters/Blake/Textures/tex_char_blake_albedo.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 1, alpha | 4.19 |
| `Drive/Resources/Characters/Blake/Textures/tex_char_blake_metal_ao_smoothness.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 1, alpha | 4.19 |
| `Drive/Resources/Characters/Blake/Textures/tex_char_blake_normal.png` | 2048×2048 | 2048×2048, ≈5.59 МБ | max 2048, compression 1, alpha, normal map | 4.19 |
| `Drive/Resources/Cars/M300/Textures/Details-1.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/M300/Textures/Leather.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/M300/Textures/Velvet-0.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/M300/Textures/Wood.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/Mercedes/Textures/Cover_df.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/Mercedes/Textures/Interior0_df.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/Mercedes/Textures/Interior1_df.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/Mercedes/Textures/Leather0_df.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/Mercedes/Textures/Leather1_df.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/Mercedes/Textures/Plastic0_df.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/Mercedes/Textures/Velvet1_df.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/Mercedes/Textures/Velvet_df.png` | 2048×2048 | 2048×2048, ≈2.80 МБ | max 2048, compression 1 | 2.10 |
| `Drive/Resources/Cars/M300/Textures/Velvet-1.png` | 2048×1024 | 2048×1024, ≈1.40 МБ | max 2048, compression 1 | 1.05 |
| `Drive/Resources/Cars/G63/int_badges2.dds` | 2048×512 | 2048×512, ≈0.70 МБ | max 2048, compression 1 | 0.52 |
| **Итого (19)** | | **63.6** | | **47.7** |

### 6.5. Normal-карты, импортированные как Default (не размер, а качество)

Не влияет на размер, но такие карты сжимаются как цветные (DXT1 без swizzle) и могут давать неверное освещение. При ручной проверке стоит заодно выставить `textureType: NormalMap` там, где материал действительно использует файл как `_BumpMap`.

| Текстура | Размер в сборке | Сейчас |
|---|---|---|
| `Drive/Resources/Planes/Piper/Leather_003_NRM.jpg` | 2048×2048 | textureType 0, compression 1 |
| `Drive/Resources/Cars/Porsche911/Leather_perfo_normal.jpeg` | 1024×1024 | textureType 0, compression 1 |
| `Drive/Resources/Cars/Porsche911/belts_normal.jpeg` | 1024×1024 | textureType 0, compression 1 |
| `Drive/Resources/Cars/Porsche911/leather_normal.jpeg` | 1024×1024 | textureType 0, compression 1 |
| `Drive/Resources/Cars/AudiS4B8/textures/Chrome_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/AudiS4B8/textures/interior_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/AudiS4B8/textures/tire_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/AudiS4B8/textures/under_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/G63/ptn_leather_016_nrml.dds` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Cabin_Leather_B1_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Cabin_Leather_C1_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_ChangeA_Tyre1_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Dashboard1_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Engine1_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Light1_normal.png` | 512×512 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Cabin_Grid_A1_normal.png` | 256×256 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Cabin_Leather_A_006_normal.png` | 256×256 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Cabin_Plastic_A1_normal.png` | 256×256 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Cabin_Wood_A1_normal.png` | 256×256 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_ChangeA_Rim1_normal.png` | 256×256 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Change_Brake1_normal.png` | 256×256 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/e50MI_Logo1_normal.png` | 256×256 | textureType 0, compression 1 |
| `Drive/Resources/Cars/MercedesE50/textures/material_normal.png` | 128×128 | textureType 0, compression 1 |

## 7. Модели

Код **читает меши** почти всех загружаемых моделей. Если выключить `isReadable`, это сломается только в сборке (в редакторе всё будет работать):
- машины — `Bodywork.cs:413`, `CarLook.cs:338`;
- самолёты — `Airplane.cs:250`;
- авианосец — `Carrier.cs:50/68`, `MeshCollider`;
- катер — `Boat.cs:79`, `CombineMeshes`;
- деревья — `Foliage.cs:348`;
- камни — `Island.cs:432`;
- весь город, фонари и знаки — `Art.Combine`, `Knockable.MergeUnder`.

Поэтому **`isReadable` на моделях не менять**. Исключения, которые можно проверить руками, — оружие `Weapons/LowPoly` и `AIM120`: их только инстанцируют. Но выигрыш там — несколько сотен КБ **оперативной памяти**, размер сборки не изменится.

Выигрыш на диске даёт `meshCompression` (сейчас `0` = Off у всех). Уровень Medium обычно уменьшает вершинные данные на 30–50 %. Модели ModelImporter в сборке оцениваются в ≈ 160 МБ, отсюда **≈ 30–60 МБ (M5), очень приблизительно**. Проверять на швах и нормалях кузова (блики) и на мелких деталях. Начинать с самых тяжёлых: LabCoupe, G63, Piper, Porsche 911, Classic50 `.dae`, TruckLevo M1035, SportCar_1. glTF-машины (glTFast) и A340 `.glb` настраиваются в glTFast отдельно, их лучше не трогать.

`importAnimation`, `importCameras` и `importLights` включены у статичных моделей: деревья, здания, авианосец, AIM120, B-1, Piper, Classic50. Выключение экономит килобайты, поэтому в сводку не входит. Если делать, то вместе с M5.

| Модель | Исходник, МБ | isReadable | meshCompression | importAnimation / Cameras / Lights | Кто читает меш из кода | Рекомендация |
|---|---:|---|---|---|---|---|
| `Drive/Resources/Cars/LabCoupe/LabCoupe.fbx` | 24.51 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/G63/G63.fbx` | 23.63 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Planes/A340/a340.glb` | 20.58 | glTFast | — | — | Airplane читает `mesh.vertices` | настройки glTFast; не трогать |
| `Drive/Resources/Planes/Piper/PiperMeridian.fbx` | 14.69 | 1 | 0 | 1 / 1 / 1 | Airplane читает `mesh.vertices` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Cars/Porsche911/porsche911.fbx` | 14.35 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/Classic50/Models/50sClassicCar#6/1950sClassicCar#6.dae` | 12.93 | 1 | 0 | 1 / 1 / 1 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Cars/TruckLevo/Meshes/SK_Truck_Levo_M1035.fbx` | 11.90 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/SportCar/Models/SportCar_1/SportCar_1.FBX` | 9.28 | 1 | 0 | 0 / 1 / 1 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы); importCameras/Lights → 0 |
| `Drive/Resources/Cars/BMWX6/BMW_X6.fbx` | 7.73 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Models/interior.obj` | 5.92 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/BmwM5/m5.glb` | 5.14 | glTFast | — | — | Bodywork/CarLook читают `mesh.vertices` | настройки glTFast; не трогать |
| `Drive/Resources/Cars/M300/Mercedes300SEL.fbx` | 4.98 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Ships/Carrier/carrier.fbx` | 4.28 | 1 | 0 | 1 / 1 / 1 | Carrier читает вершины и строит MeshCollider | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Cars/Mercedes/Mercedes560SEL.fbx` | 4.02 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/AudiR8/AudiR8.fbx` | 3.62 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Models/body.obj` | 3.41 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Weapons/AIM120/aim120.fbx` | 3.00 | 1 | 0 | 1 / 1 / 1 | Missile: только Instantiate + bounds — проверить | isReadable → 0 после проверки; meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Chestnut_2.fbx` | 2.11 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Buildings/Models/building_2.fbx` | 2.10 | 1 | 0 | 1 / 1 / 1 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Chestnut_1.fbx` | 2.07 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Chestnut_3.fbx` | 1.89 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Models/lights.obj` | 1.86 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Trees/Models/Spruce_2.fbx` | 1.75 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Spruce_3.fbx` | 1.74 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Spruce_4.fbx` | 1.68 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Ash_3.fbx` | 1.60 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Cars/Tocus/Tocus.FBX` | 1.58 | 1 | 0 | 1 / None / None | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы); importAnimation → 0 |
| `Drive/Resources/Env/Trees/Models/Birch_3.fbx` | 1.57 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Spruce_1.fbx` | 1.51 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Ash_1.fbx` | 1.42 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Ash_2.fbx` | 1.40 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Birch_2.fbx` | 1.35 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Models/chrome.obj` | 1.30 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/Touring/Meshes/Chev.FBX` | 1.25 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Models/trim.obj` | 1.22 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Buildings/Models/building_6.fbx` | 1.20 | 1 | 0 | 1 / 1 / 1 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Planes/B1/b1.fbx` | 1.11 | 1 | 0 | 1 / 1 / 1 | Airplane читает `mesh.vertices` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Models/wheel_rim.obj` | 0.93 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Buildings/Models/building_1.fbx` | 0.93 | 1 | 0 | 1 / 1 / 1 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Trees/Models/Birch_1.fbx` | 0.92 | 1 | 0 | 1 / 1 / 1 | Foliage делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Characters/Blake/FBX/The Adventurer Blake.fbx` | 0.79 | 0 | 0 | 1 / 1 / 1 | SkinnedMesh, isReadable уже 0 | meshCompression Low/Medium (проверить швы); importCameras/Lights → 0 |
| `Drive/Resources/Boats/Modern/Boat.fbx` | 0.65 | 1 | 0 | 1 / 1 / 1 | Boat делает `CombineMeshes` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Cars/TruckLow/Mesh/Truck_LowPoly.fbx` | 0.60 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Weapons/LowPoly/Models/M1911.fbx` | 0.50 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/Touring/Meshes/Tires.FBX` | 0.47 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Models/glass.obj` | 0.44 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/MercedesE50/scene.gltf` | 0.33 | glTFast | — | — | Bodywork/CarLook читают `mesh.vertices` | настройки glTFast; не трогать |
| `Drive/Resources/Cars/Military/Fbx/Military combat Jeep.fbx` | 0.28 | 1 | 0 | 1 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы); importAnimation → 0 |
| `Drive/Resources/Weapons/LowPoly/Models/M4_8.fbx` | 0.25 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/Stylized/Car2.fbx` | 0.19 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Weapons/LowPoly/Models/AK74.fbx` | 0.19 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Signs/Models/Signs Pack.FBX` | 0.18 | 1 | 0 | 1 / None / None | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы); importAnimation → 0 |
| `Drive/Resources/Cars/Stylized/Sedan1.fbx` | 0.15 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Weapons/LowPoly/Models/M107.fbx` | 0.14 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/Stylized/SportCar2.fbx` | 0.13 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/Stylized/MicroBus4.fbx` | 0.13 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/Stylized/Jeep2.fbx` | 0.13 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Buildings/Models/roof_building.fbx` | 0.12 | 1 | 0 | 1 / 1 / 1 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Weapons/LowPoly/Models/Uzi.fbx` | 0.12 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Boulders/mesh/rock3_LOD1.fbx` | 0.11 | 1 | 0 | 0 / 0 / 0 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/SportCar/Wheels/Tire/Sport_Tire.fbx` | 0.10 | 1 | 0 | 0 / 1 / 1 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы); importCameras/Lights → 0 |
| `Drive/Resources/Env/Boulders/mesh/rock6_LOD1.fbx` | 0.10 | 1 | 0 | 0 / 0 / 0 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Weapons/LowPoly/Models/Bennelli_M4.fbx` | 0.09 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/StreetLights/Meshes/Street Lighting 4/Street.Lighting.4.fbx` | 0.09 | 1 | 0 | 0 / 0 / 0 | `Art.Combine` сливает меши фонарей | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Cars/AudiS4B8/scene.gltf` | 0.09 | glTFast | — | — | Bodywork/CarLook читают `mesh.vertices` | настройки glTFast; не трогать |
| `Drive/Resources/Cars/SportCar/Wheels/Ring/Ring_1.fbx` | 0.08 | 1 | 0 | 0 / 1 / 1 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы); importCameras/Lights → 0 |
| `Drive/Resources/Cars/Van/van.FBX` | 0.07 | 1 | 0 | 0 / 0 / 0 | Bodywork/CarLook читают `mesh.vertices` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Boulders/mesh/rock4_LOD1.fbx` | 0.06 | 1 | 0 | 0 / 0 / 0 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Boulders/mesh/rock1_LOD1.fbx` | 0.06 | 1 | 0 | 0 / 0 / 0 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Boulders/mesh/rock2_LOD1.fbx` | 0.06 | 1 | 0 | 0 / 0 / 0 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Buildings/Models/roof_building_small.fbx` | 0.06 | 1 | 0 | 1 / 1 / 1 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Models/wheel_tyre.obj` | 0.05 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Boulders/mesh/rock5_LOD1.fbx` | 0.05 | 1 | 0 | 0 / 0 / 0 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/StreetLights/Meshes/Street Lighting 2/Street.Lighting.2.fbx` | 0.05 | 1 | 0 | 0 / 0 / 0 | `Art.Combine` сливает меши фонарей | meshCompression Low/Medium (проверить швы) |
| `Drive/Resources/Env/Rocks/3d model/Rock/rock_set_04.fbx` | 0.03 | 1 | 0 | 1 / 1 / 1 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Rocks/3d model/Rock/rock_set_01.fbx` | 0.03 | 1 | 0 | 1 / 1 / 1 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Rocks/3d model/Rock/rock_set_03.fbx` | 0.03 | 1 | 0 | 1 / 1 / 1 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Rocks/3d model/Rock/rock_set_02.fbx` | 0.03 | 1 | 0 | 1 / 1 / 1 | Island читает `vertices/triangles` | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Env/Buildings/Models/roof_chimney.fbx` | 0.02 | 1 | 0 | 1 / 1 / 1 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы); importAnimation → 0; importCameras/Lights → 0 |
| `Drive/Resources/Models/headlights.obj` | 0.02 | 1 | 0 | 0 / 0 / 0 | Supercar: Bodywork/CarLook | meshCompression Low/Medium (проверить швы) |

## 8. Аудио

В сборке ≈ 40.9 МБ аудио, почти всё — радио (7 треков mp3, 3–4 минуты). Настройки у радио правильные: `Streaming`, Vorbis 0.55, `preloadAudioData: 0`. Длинных клипов с `DecompressOnLoad` в сборке нет: 71-секундный `TramIdleLoop` лежит вне Resources. Клипов без сжатия (PCM или ADPCM) тоже нет.

| Клип | Длительность | Каналы / Гц | loadType | Формат, quality | preload | ≈ в сборке, МБ | Рекомендация | Экономия диска, МБ |
|---|---:|---|---|---|---|---:|---|---:|
| `Drive/Resources/Radio/Track1_GorillaZoe.mp3` | 256.1 с | 2* / 44100* | Streaming | Vorbis, 0.55 | 0 | 6.69 | quality 0.55 → 0.40 (на слух) | 1.08 |
| `Drive/Resources/Radio/Track7_505.mp3` | 254.7 с | 2* / 44100* | Streaming | Vorbis, 0.55 | 0 | 6.65 | quality 0.55 → 0.40 (на слух) | 1.08 |
| `Drive/Resources/Radio/Track2_Kasheshov.mp3` | 248.9 с | 2* / 44100* | Streaming | Vorbis, 0.55 | 0 | 6.50 | quality 0.55 → 0.40 (на слух) | 1.05 |
| `Drive/Resources/Radio/Track4_Brodyaga.mp3` | 245.6 с | 2* / 44100* | Streaming | Vorbis, 0.55 | 0 | 6.41 | quality 0.55 → 0.40 (на слух) | 1.04 |
| `Drive/Resources/Radio/Track3_Mugu.mp3` | 227.3 с | 2* / 44100* | Streaming | Vorbis, 0.55 | 0 | 5.93 | quality 0.55 → 0.40 (на слух) | 0.96 |
| `Drive/Resources/Radio/Track6_OldYellowBricks.mp3` | 191.5 с | 2* / 44100* | Streaming | Vorbis, 0.55 | 0 | 5.00 | quality 0.55 → 0.40 (на слух) | 0.81 |
| `Drive/Resources/Radio/Track5_LetItHappen.mp3` | 100.7 с | 2* / 44100* | Streaming | Vorbis, 0.55 | 0 | 2.63 | quality 0.55 → 0.40 (на слух) | 0.43 |
| `Drive/Resources/Sounds/2CV6Ignition.wav` | 6.7 с | 1 / 96000 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.28 | sampleRate Override 44100 | 0.15 |
| `Drive/Resources/Sounds/2CV6MotorNoStart2.wav` | 6.7 с | 1 / 96000 | DecompressOnLoad | Vorbis, 1.00 | 0 | 0.28 | sampleRate Override 44100 | 0.15 |
| `Drive/Resources/Sounds/2CV6EngineOff.wav` | 3.3 с | 1 / 96000 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.14 | sampleRate Override 44100 | 0.08 |
| `Drive/Resources/Sounds/2CV6MotorNoStart.wav` | 1.9 с | 1 / 96000 | DecompressOnLoad | Vorbis, 1.00 | 0 | 0.08 | sampleRate Override 44100 | 0.04 |
| `Drive/Resources/Sounds/2CV6KeysOut.wav` | 1.9 с | 1 / 96000 | DecompressOnLoad | Vorbis, 1.00 | 0 | 0.08 | sampleRate Override 44100 | 0.04 |
| `Drive/Resources/Sounds/2CV6HandbrakeOff.wav` | 1.5 с | 1 / 96000 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.06 | sampleRate Override 44100 | 0.03 |
| `Drive/Resources/Sounds/2CV6HandbrakeOn.wav` | 1.5 с | 1 / 96000 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.06 | sampleRate Override 44100 | 0.03 |
| `Drive/Resources/Sounds/Foot/reloadSound.wav` | 1.3 с | 2 / 44100 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.05 | — | 0.00 |
| `Drive/Resources/Sounds/ElevatorButton.wav` | 0.6 с | 1 / 96000 | DecompressOnLoad | Vorbis, 1.00 | 0 | 0.03 | sampleRate Override 44100 | 0.01 |
| `Drive/Resources/Sounds/Foot/pullweapon.wav` | 1.1 с | 1 / 32000 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.02 | — | 0.00 |
| `Drive/Resources/Sounds/Foot/playerSound/die-01.wav` | 0.7 с | 1 / 44100 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.01 | — | 0.00 |
| `Drive/Resources/Sounds/Foot/shotSound.wav` | 0.1 с | 2 / 48000 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.00 | — | 0.00 |
| `Drive/Resources/Sounds/Foot/hitMarker.wav` | 0.0 с | 2 / 44100 | DecompressOnLoad | Vorbis, 1.00 | 1 | 0.00 | — | 0.00 |

\* для mp3 длительность оценена по битрейту первого фрейма; частота и каналы не определялись.

- **S3 (точно безопасно):** 2CV6-звуки записаны с частотой 96 кГц. `sampleRateSetting: Override 44100` — на слух разницы нет, экономия ≈ 0.6 МБ.
- **M4 (на слух):** радио на quality 0.40, ≈ 6.5 МБ.

## 9. Порядок применения (для локального агента)

1. **Базовый замер.** Собрать через `BuildGame` → `../Autobahn.app`. Записать `du -sk Autobahn.app` и `du -sk Autobahn.app/Contents/Resources/Data`, а из `Editor.log` сохранить блок «Build Report» (Used Assets по размерам и «Textures / Meshes / Sounds … %»).
2. **S1 + S2:** перенести файлы из §5.1 в `Assets/Drive/Parked/<тот же относительный путь>` через `AssetDatabase.MoveAsset`, как делает `BuildSlim`. **S3:** выставить Override 44100 у 2CV6-звуков. Собрать и замерить. Ожидание: −35.0 МБ.
3. **M1** (окружение), потом **M2** (машины и Blake): выставить `maxTextureSize` и compression, как в §6.2–6.4. Собрать, замерить, пройтись визуально.
4. **M3** (QA-здания), **M4** (радио), **M5** (`meshCompression`) — каждый шаг отдельным замером.
5. **M6** (LZ4HC) — по желанию, последним: меняет только упаковку и время загрузки.

---
_Сгенерировано статическим анализом (Python-скрипт в песочнице агента, в репозиторий не добавлен). Числа «≈» — оценки, а не замеры._
