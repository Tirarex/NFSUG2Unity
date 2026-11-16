# NFS Underground → Unity

Импортер файлов **Need for Speed Underground (2003, PC)** прямо в Unity-проект: города, текстуры,
дорожная сеть и трафик-машины — без экспорта в Collada/FBX посередине. Форматы читает собственный
код (`Assets/NFSU/Editor/`), Unity сама импортирует `.dds`, `.mesh` и `.mat` как обычные ассеты.

## Что где лежит

| путь | что это |
| --- | --- |
| `Need for Speed Underground/` | установка игры v1.4 — исходные `.BUN` / `.BIN` / `.lzc` |
| `UnityProj/NFS UG IMPORT/` | Unity-проект, весь код в `Assets/NFSU/` |
| `AssetDumper/` | NFSTools AssetDumper — используется только как «оракл» для сверки, не часть пайплайна |

Нужна Unity **6000.3.20f1** (Built-in Render Pipeline). Пути с пробелами — в скриптах всё в кавычках.

## Первый запуск: загрузить всю карту

1. Открыть `UnityProj/NFS UG IMPORT` в Unity 6000.3.20f1.
2. Создать новую сцену и сохранить как `Assets/NFSU/Scenes/MY_MAP.unity` — импорт идёт в **открытую** сцену.
3. Меню **NFSU ▸ Import Map From Bundle**.
4. Кнопка **Track RA (whole city)** — в списке бандлов появится `Tracks/STREAML1RA.BUN` (88 МБ, 423 секции,
   ~21 400 объектов). Для общих текстур (заборы, стёкла, тени) добавить через **Add bundle...**:
   `Global/GLOBALA.BUN` и `Global/GLOBALB.BUN`.
5. Настройки по дефолту и подходят: `Section ids` пусто (все секции), `Max sections` = 0 (без лимита),
   `Alpha mode` = Auto, `Skip mesh prefixes` = SKYDOME.
   Первый раз на слабой машине — поставить `Max sections` = 100–150 и потом дособрать остальное.
6. **Analyse** (несколько секунд, покажет отчёт: секции, инстансы, геометрия, текстуры), затем
   **Import into scene**. На всю карту — несколько минут, Unity по дороге импортирует ~500 текстур.
7. Сохранить сцену (`Ctrl+S`). Ассеты: `Assets/NFSU/Textures`, `.../Materials`, `.../Meshes`,
   объекты в корне `NFSU_MAP` с слоями `NFSU_Terrain / Building / Wall / Decor / Sign`.

В отчёте импорта смотреть на `unwritten textures 0` и строку `containment ... checked, N out` —
это проверка, что объекты стоят там, где их записала игра (матрицы расстановки).

Как должно выглядеть: фонари и блики светятся, заборы/листва/балконы сквозятся, бетон, кирпич и
асфальт — без дырок, дорога с коллайдером.

## Остальные меню

- **NFSU ▸ Import Traffic Cars** — 15 трафик-машин (`4DR_SEDAN`, `BUS`, `TAXI`, …) с mesh-ами,
  материалами, крутящимися колёсами, LOD и префабами в `Assets/NFSU/TrafficCars/`.
- **NFSU ▸ Import Traffic Paths** — дорожная поверхность и коридоры трафика из `Tracks/TRACKB####.lzc`.
- **NFSU ▸ Self Test - …** — дампы форматов в CSV и готовые прогоны; работают и без GUI.
- Готовые сцены после прогонов: `Assets/NFSU/Scenes/RA_batch.unity`, `RX_smoke.unity`,
  `CARS.unity`, `TRAFFIC_all.unity`.

## Без открытия редактора

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe" -batchmode -nographics `
  -projectPath "UnityProj\NFS UG IMPORT" `
  -executeMethod NFSU.NfsuSelfTest.RunAll -logFile "$env:TEMP\unity.log" -quit
```

## Полезно знать

- Прозрачность в форматах **не хранится** — её подбирает `NfsuAlphaClassifier` (режимы opaque /
  cutout / blend / additive зашиты в имя `.mat`). `Alpha mode = Auto` почти всегда правильный.
- `Flip triangle winding` оставить выключенным: порядок индексов в бандле уже подходит Unity.
- `Clear generated assets first` включать после правок правил импорта, иначе старые `.mat`/`.dds`
  перезатрут результат.
- `.meta`, `Library/`, `Temp/`, `Logs/`, `UserSettings/` не трогать и не коммитить.
