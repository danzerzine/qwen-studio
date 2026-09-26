<div align="center">

<img src="src/QwenStudio/app.ico" width="72" alt="">

# Qwen Studio

**Пульт управления локальной LLM на Windows.** Запускает llama.cpp в нужном режиме одной кнопкой,
показывает загрузку видеокарты и скорость, держит рядом Open WebUI, OpenCode и ComfyUI для картинок.

[Возможности](#возможности) · [Быстрый старт](#быстрый-старт) · [Режимы](#режимы) · [Инструменты](#инструменты) · [Сборка](#сборка-из-исходников) · [English](#in-english)

<img src="docs/screenshot.png" alt="Qwen Studio: сервер в режиме «Параллельно», 4 слота заняты, график за 24 часа" width="900">

</div>

Работает с любой GGUF-моделью: Qwen, Llama, Gemma, Mistral и другими. Название историческое:
Studio вырос вокруг Qwen3.8-27B на видеокарте с 16 ГБ, и режимы по умолчанию подобраны под такую связку.

## Возможности

- **Режимы одной кнопкой.** Агент, чат, без размышлений, зрение, параллельные запросы. Режим — это набор
  аргументов llama-server в `profiles.json`. Переключение перезапускает сервер. Если идёт запрос, Studio
  сначала спросит.
- **Основная и запасная модель.** Любой режим можно запустить на второй модели: например, на прошлой
  проверенной версии, если новая ведёт себя странно.
- **Видно, что происходит.** Скорость генерации и промпта, занятые слоты, память, загрузка, питание и
  температура видеокарты. Живой график за 5 минут, история за 24 часа и статистика по дням.
- **Видеопамять одна на всех.** Перед стартом Studio выгружает модели Ollama, LM Studio и ComfyUI, чтобы две
  модели не делили одну карту. Если сервер падает с ошибкой CUDA, Studio его перезапускает.
- **Доступ из сети и API-ключ.** Ключ генерируется одной кнопкой, конфиг OpenCode обновляется сам.
  Доступ из локальной сети — переключателем. Правила брандмауэра открывают порты только для частной сети
  и своей подсети.
- **Обновления.** Studio скачивает свежую CUDA-сборку llama.cpp с GitHub в отдельную папку, а текущая
  остаётся на месте. Open WebUI обновляется через `uv`.
- **Мелочи.** Автозапуск вместе с Windows (сервер тоже может стартовать сам), архив старых журналов,
  светлая и тёмная тема. Закрытие окна не останавливает сервер, если вы этого не хотите.

## Быстрый старт

Нужно: Windows 10/11 x64, видеокарта NVIDIA и [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

1. **Соберите Studio** (см. [сборку](#сборка-из-исходников)) или возьмите `QwenStudio.exe` из Releases.
   Положите его в отдельную папку вместе с `profiles.json` и `server_config.example.env`.
2. **Настройки.** Переименуйте `server_config.example.env` в `server_config.env`. Все поля можно заполнить
   и из самого приложения, во вкладке «Настройки».
3. **Модель.** Скачайте GGUF (например, с [Hugging Face](https://huggingface.co/models?library=gguf)) и
   укажите путь в `MODEL_PATH` или в «Настройки → Сервер и модели».
4. **llama.cpp.** В «Настройки → Обновления» нажмите «Проверить», затем «Скачать». Studio положит сборку
   в `llama-bNNNN\` рядом с собой и сам её найдёт. Свою сборку можно указать в `SERVER_EXE`.
5. **Запуск.** Выберите режим слева и нажмите «Запустить». API сервера:
   `http://127.0.0.1:8080/v1`, модель `local`. Это имя задаёт `--alias` в профилях.

Раскладка папки:

```
QwenStudio.exe
profiles.json          режимы сервера
server_config.env      пути, порт, ключ (не публикуйте его)
llama-b1xxxx\          llama.cpp, скачивается из «Обновлений»
models\                модели, если держать их рядом
logs\studio\           журналы сервера, статистика, настройки окна
```

## Режимы

Режимы описаны в `profiles.json`. Их можно менять и добавлять: «Настройки → Обслуживание → Профили →
Изменить», затем «Перечитать».

| Режим | Контекст | Для чего |
|---|---|---|
| Агент | 128K | OpenCode и другие агенты: длинный контекст, полные размышления |
| Чат | 64K | быстрые ответы, размышления короче |
| Без размышлений | 64K | перевод, короткие ответы, скрипты, пакетные прогоны |
| Зрение | 64K | чат с картинками; нужен `mmproj`-файл модели (`MMPROJ_PATH`) |
| Параллельно | 4 × 8K | четыре запроса одновременно: пакетная обработка, несколько клиентов |

Переключатель **Основная / Запасная** запускает тот же режим на `FALLBACK_MODEL_PATH` (и
`FALLBACK_SERVER_EXE`, если он задан). Для запасной модели Studio убирает аргументы `--spec-*`,
ограничивает контекст 96K и выключает кэш промпта. Так проще откатиться на старую модель или сборку.

Значения по умолчанию (`-ctk/-ctv q4_0`, `-ngl 999`, `--fit off`) рассчитаны на модель ~27B в Q3/IQ4
на карте с 16 ГБ. Для другой связки подберите `ctx`, тип KV-кэша и `-np`. Если модель и сборка
llama.cpp поддерживают MTP, добавьте в `args` `"--spec-type", "draft-mtp", "--spec-draft-n-max", "1"`:
это ускоряет генерацию.

## Инструменты

### Open WebUI — чат в браузере

```bash
uv tool install --python 3.11 open-webui
```

Studio ищет `%USERPROFILE%\.local\bin\open-webui.exe` (туда его ставит `uv`) и запускает его на
`WEBUI_PORT` (3000), уже подключённым к серверу и с текущим API-ключом. Кнопка «Открыть» поднимает
WebUI и открывает браузер. После смены ключа Studio сам пропишет новый ключ в настройки WebUI.

### OpenCode — агент для кода

Поставьте [OpenCode](https://opencode.ai): приложение для ПК или `npm i -g opencode-ai`. Studio
откроет то, что найдёт. Подключение к локальному серверу в `%USERPROFILE%\.config\opencode\opencode.jsonc`:

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "provider": {
    "local": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "Qwen Studio",
      "options": { "baseURL": "http://127.0.0.1:8080/v1", "apiKey": "ваш LLAMA_API_KEY" },
      "models": { "local": { "name": "Локальная модель" } }
    }
  }
}
```

Когда вы меняете ключ в Studio, он заменяется и в этом файле. Старая версия файла сохраняется рядом.

### Картинки — ComfyUI

1. Скачайте портативный [ComfyUI](https://github.com/comfyanonymous/ComfyUI/releases) и распакуйте.
2. Укажите папку в «Настройки → Сервер и модели → ComfyUI» (или `COMFYUI_DIR`). Подойдёт и git-установка
   с `venv`.
3. Модель картинок (Qwen-Image, FLUX, SDXL…) ставится через шаблоны самого ComfyUI.

Studio запускает ComfyUI на порту 8188 и открывает его в браузере. Большим моделям картинок нужна почти
вся видеопамять, поэтому Studio предложит остановить LLM-сервер. Перед стартом сервера он попросит
ComfyUI выгрузить модели. Во время генерации Studio ничего не остановит, не спросив вас.

## Настройки

Все параметры лежат в `server_config.env`. Список с комментариями — в
[`server_config.example.env`](server_config.example.env).

| Ключ | Что это |
|---|---|
| `LLAMA_API_KEY` | ключ API; пусто — без ключа |
| `HOST`, `PORT` | `127.0.0.1` — только этот ПК, `0.0.0.0` — и локальная сеть; порт сервера |
| `WEBUI_PORT` | порт Open WebUI |
| `SERVER_EXE`, `MODEL_PATH` | llama-server и модель; пустой `SERVER_EXE` — самая свежая `llama-bNNNN\` |
| `FALLBACK_SERVER_EXE`, `FALLBACK_MODEL_PATH` | запасная сборка и модель |
| `MMPROJ_PATH` | модуль зрения для режима «Зрение» |
| `COMFYUI_DIR`, `COMFYUI_ARGS` | ComfyUI и его дополнительные аргументы |
| `POWER_LIMIT_W` | лимит мощности видеокарты для кнопки в «Обслуживании»; пусто — кнопки нет |

## Сборка из исходников

Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
build.cmd
```

Результат — `dist\QwenStudio.exe` с шаблонами конфигов. Это WPF-приложение на .NET 8 без внешних
зависимостей. Код: `src/QwenStudio` — окно в `MainWindow.xaml(.cs)`, логика в `Core/`. Все размеры, цвета
и отступы берутся из дизайн-системы, она описана в [`DESIGN.md`](src/QwenStudio/DESIGN.md).

Пока только Windows, NVIDIA (данные о карте берутся через NVML) и русский интерфейс.

## In English

**Qwen Studio** is a Windows control panel for a local llama.cpp server. It starts the server in one of
several modes (`profiles.json`): agent (128K), chat, no-thinking, vision, and 4 parallel slots. It can
run any mode on a fallback model. It shows live GPU load, VRAM, power and tokens/s, and keeps 24-hour
and daily statistics. It launches Open WebUI, OpenCode and ComfyUI, and unloads Ollama, LM Studio and
ComfyUI models before starting so the models don't fight over VRAM. It manages the API key, LAN access
and firewall rules, and downloads fresh llama.cpp CUDA builds. Works with any GGUF model.
The UI is in Russian.

Quick start: build with `build.cmd` (.NET 8 SDK), copy `server_config.example.env` to `server_config.env`,
set `MODEL_PATH`, then download llama.cpp from *Настройки → Обновления* and press *Запустить*.
The OpenAI-compatible API is at `http://127.0.0.1:8080/v1` with model id `local`.

## Лицензия

[MIT](LICENSE)
