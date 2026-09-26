# Какой llama.cpp скачать

[← к README](../README.md) · [English version ↓](#which-llamacpp-build-to-download)

Qwen Studio не содержит llama.cpp: он запускает `llama-server.exe`, который вы скачиваете сами или кнопкой
в «Настройки → Обновления». Эта страница поможет выбрать сборку, установить её вручную и откатиться, если
новая версия поведёт себя хуже.

## Самый простой путь

«Настройки → Обновления» → «Проверить» → «Скачать bNNNN». Studio возьмёт последнюю официальную сборку
**CUDA 12** для Windows x64 и её библиотеки CUDA, положит их в `llama-bNNNN\` рядом с собой и начнёт
использовать при следующем запуске сервера. Предыдущая папка остаётся на месте.

Этого достаточно для любой видеокарты NVIDIA с драйвером, поддерживающим CUDA 12. Всё, что ниже, нужно,
если у вас другая видеокарта, вы хотите CUDA 13 или конкретную версию.

## Какой файл выбрать

Все сборки лежат на странице [ggml-org/llama.cpp → Releases](https://github.com/ggml-org/llama.cpp/releases).
Номер сборки (`b11201` и т. п.) растёт с каждым изменением, новые выходят почти каждый день.
Все они помечены как «Pre-release», это нормально. Для Windows там такие архивы (на примере b11201):

| Архив | Для чего | Размер |
|---|---|---|
| `llama-bNNNN-bin-win-cuda-12.4-x64.zip` + `cudart-llama-bin-win-cuda-12.4-x64.zip` | NVIDIA, CUDA 12. Эту пару качает Studio | 250 + 370 МБ |
| `llama-bNNNN-bin-win-cuda-13.4-x64.zip` + `cudart-llama-bin-win-cuda-13.4-x64.zip` | NVIDIA, CUDA 13 | 145 + 400 МБ |
| `llama-bNNNN-bin-win-vulkan-x64.zip` | AMD, Intel и NVIDIA через Vulkan | 30 МБ |
| `llama-bNNNN-bin-win-rocm-10.0-x64.zip` | AMD Radeon через ROCm | 245 МБ |
| `llama-bNNNN-bin-win-sycl-x64.zip` | Intel Arc через SYCL | 115 МБ |
| `llama-bNNNN-bin-win-openvino-…-x64.zip` | Intel через OpenVINO | 85 МБ |
| `llama-bNNNN-bin-win-cpu-x64.zip` | без видеокарты (медленно) | 18 МБ |
| `…-win-…-arm64.zip` | Windows на ARM (Snapdragon): CPU, CUDA 13, OpenCL Adreno | — |

Архив `cudart-…` — это библиотеки CUDA (`cudart64_*.dll`, `cublas64_*.dll`). Без них CUDA-сборка
не запустится, если на компьютере не установлен CUDA Toolkit той же версии.

### NVIDIA: CUDA 12 или CUDA 13

Откройте терминал и выполните `nvidia-smi`. В правом верхнем углу есть строка `CUDA Version: …` — это
самая новая версия CUDA, которую поддерживает ваш драйвер. Сборка должна быть не новее этого числа.

| Видеокарта | Что брать |
|---|---|
| GTX 9xx, GTX 10xx, Titan V (Maxwell, Pascal, Volta) | только **CUDA 12**: CUDA 13 эти карты уже не поддерживает |
| RTX 20xx, 30xx, 40xx | **CUDA 12** или **CUDA 13** — скорость почти одинаковая; CUDA 12 не требует свежего драйвера |
| RTX 50xx | **CUDA 13** (драйвер 580 или новее): официальная сборка CUDA 12.4 вышла раньше этих карт. Кнопка Studio ставит CUDA 12, поэтому CUDA 13 скачайте вручную |

Если сомневаетесь и карта не RTX 50xx, берите CUDA 12: её же ставит Studio.

### Не NVIDIA

Сервер запустится с Vulkan-, ROCm-, SYCL- и CPU-сборками: режимы, чат и агент работают так же.
Но панель видеокарты в Studio (загрузка, видеопамять, температура, график) читает данные через
драйвер NVIDIA, поэтому на других картах она останется пустой. Аргументы в `profiles.json`
(`-ngl`, `-fa`, типы KV-кэша) подходят для всех бэкендов, но размер контекста подберите под свою память.

## Установка вручную

1. Скачайте архив сборки и, для CUDA, парный `cudart-…` той же версии CUDA.
2. Распакуйте **оба** в одну папку рядом с `QwenStudio.exe` и назовите её **`llama-bNNNN`**, где NNNN — номер
   сборки, например `llama-b11201\`. Вложенная папка внутри тоже подойдёт: Studio ищет
   `llama-server.exe` во всех подпапках.
3. Проверьте версию: `llama-b11201\llama-server.exe --version`.
4. Перезапустите сервер в Studio. Он берёт папку `llama-b*` с самым большим номером.

Если папка называется иначе (например, `llama-vulkan\`) или лежит в другом месте, укажите путь к
`llama-server.exe` в `SERVER_EXE` («Настройки → Сервер и модели → Сервер»). Тогда Studio будет
использовать именно её, а кнопка «Проверить» будет сравнивать свежий релиз с номером этой сборки.

## Закрепить версию и откатиться

- **Закрепить.** Укажите `SERVER_EXE`. Новые папки `llama-b*` больше не будут подхватываться сами.
- **Откатиться.** Старые папки Studio не удаляет. Укажите в `SERVER_EXE` путь к предыдущей и перезапустите
  сервер. Лишние папки можно просто удалить, когда новая версия проверена.
- **Держать две версии.** Режим «Запасная» использует `FALLBACK_SERVER_EXE` (если пусто — ту же сборку,
  что и основной). Удобно: новая сборка в основном режиме, проверенная старая — в запасном.

## Когда обновляться

- **Новая модель не загружается.** Ошибка вида `unknown model architecture` означает, что сборка старше
  модели. Поддержку новых архитектур добавляют в llama.cpp в первые дни после выхода модели.
- **Нужна новая функция.** Например, MTP-ускорение (`--spec-type draft-mtp`) есть только в свежих сборках.
  Проверить, знает ли сборка аргумент: `llama-server.exe --help | findstr spec-type`.
- **Исправления скорости и ошибок.** Список изменений есть в описании каждого релиза.

Если всё работает, обновляться каждый день не нужно. Новые сборки иногда приносят регрессии, поэтому
предыдущую папку стоит держать, пока новая не проверена на ваших задачах.

---

# Which llama.cpp build to download

[← back to README](../README.md) · [Русская версия ↑](#какой-llamacpp-скачать)

Qwen Studio does not bundle llama.cpp. It runs a `llama-server.exe` that you download yourself or with the
button in *Настройки → Обновления* (Settings → Updates). This page helps you pick a build, install it by hand
and roll back if a new version behaves worse.

## The easy way

*Настройки → Обновления* (Settings → Updates) → *Проверить* (Check) → *Скачать bNNNN* (Download). Studio
takes the latest official **CUDA 12** build for Windows x64 plus its CUDA libraries, puts them into
`llama-bNNNN\` next to itself and uses it the next time the server starts. The previous folder is kept.

That is enough for any NVIDIA card whose driver supports CUDA 12. Read on if you have a different GPU,
want CUDA 13 or need a specific version.

## Which file to pick

All builds are on [ggml-org/llama.cpp → Releases](https://github.com/ggml-org/llama.cpp/releases). The build
number (`b11201` and so on) grows with every change; new builds appear almost daily. The Windows archives
look like this (b11201 as an example). They are all marked *Pre-release*; that is normal.

| Archive | For | Size |
|---|---|---|
| `llama-bNNNN-bin-win-cuda-12.4-x64.zip` + `cudart-llama-bin-win-cuda-12.4-x64.zip` | NVIDIA, CUDA 12. Studio downloads this pair | 250 + 370 MB |
| `llama-bNNNN-bin-win-cuda-13.4-x64.zip` + `cudart-llama-bin-win-cuda-13.4-x64.zip` | NVIDIA, CUDA 13 | 145 + 400 MB |
| `llama-bNNNN-bin-win-vulkan-x64.zip` | AMD, Intel and NVIDIA via Vulkan | 30 MB |
| `llama-bNNNN-bin-win-rocm-10.0-x64.zip` | AMD Radeon via ROCm | 245 MB |
| `llama-bNNNN-bin-win-sycl-x64.zip` | Intel Arc via SYCL | 115 MB |
| `llama-bNNNN-bin-win-openvino-…-x64.zip` | Intel via OpenVINO | 85 MB |
| `llama-bNNNN-bin-win-cpu-x64.zip` | no GPU (slow) | 18 MB |
| `…-win-…-arm64.zip` | Windows on ARM (Snapdragon): CPU, CUDA 13, OpenCL Adreno | — |

The `cudart-…` archive holds the CUDA runtime libraries (`cudart64_*.dll`, `cublas64_*.dll`). A CUDA build
will not start without them unless the matching CUDA Toolkit is installed.

### NVIDIA: CUDA 12 or CUDA 13

Open a terminal and run `nvidia-smi`. The top-right corner shows `CUDA Version: …`: the newest CUDA your
driver supports. The build must not be newer than that.

| GPU | What to get |
|---|---|
| GTX 9xx, GTX 10xx, Titan V (Maxwell, Pascal, Volta) | **CUDA 12** only: CUDA 13 no longer supports these cards |
| RTX 20xx, 30xx, 40xx | **CUDA 12** or **CUDA 13**, speed is about the same; CUDA 12 does not need a new driver |
| RTX 50xx | **CUDA 13** (driver 580 or newer): the official CUDA 12.4 build predates these cards. Studio's button installs CUDA 12, so download CUDA 13 by hand |

When in doubt and the card is not an RTX 50xx, take CUDA 12: it is what Studio installs.

### Not NVIDIA

The server runs fine with Vulkan, ROCm, SYCL and CPU builds: modes, chat and the coding agent work the
same. Studio's GPU panel (load, video memory, temperature, chart) reads its data through the NVIDIA driver,
so on other cards it stays empty. The arguments in `profiles.json` (`-ngl`, `-fa`, KV cache types) work on
every backend, but size the context to your card's memory.

## Installing by hand

1. Download the build archive and, for CUDA, the matching `cudart-…` archive for the same CUDA version.
2. Unpack **both** into one folder next to `QwenStudio.exe` and name it **`llama-bNNNN`**, where NNNN is the
   build number, for example `llama-b11201\`. A nested folder inside is fine: Studio searches all
   subfolders for `llama-server.exe`.
3. Check the version: `llama-b11201\llama-server.exe --version`.
4. Restart the server in Studio. It picks the `llama-b*` folder with the highest number.

If the folder has another name (say, `llama-vulkan\`) or lives elsewhere, put the path to `llama-server.exe`
into `SERVER_EXE` (*Настройки → Сервер и модели → Сервер*, Settings → Server and models → Server). Studio
then always uses that build, and *Проверить* (Check) compares the latest release against its number.

## Pinning a version and rolling back

- **Pin.** Set `SERVER_EXE`. New `llama-b*` folders are no longer picked up automatically.
- **Roll back.** Studio never deletes old folders. Point `SERVER_EXE` at the previous one and restart the
  server. Delete the extra folders yourself once the new version has proven itself.
- **Keep two versions.** The *Запасная* (Fallback) mode uses `FALLBACK_SERVER_EXE` (empty means the same
  build as the main mode). A handy setup: the new build in the main mode, the proven old one in Fallback.

## When to update

- **A new model does not load.** An error like `unknown model architecture` means the build is older than
  the model. llama.cpp usually adds new architectures within days of a model's release.
- **You need a new feature.** For example, MTP speed-up (`--spec-type draft-mtp`) exists only in recent
  builds. To check whether a build knows an argument: `llama-server.exe --help | findstr spec-type`.
- **Speed and bug fixes.** Every release lists its changes.

If everything works, there is no need to update daily. New builds occasionally bring regressions, so keep
the previous folder until the new one has passed your own workloads.
