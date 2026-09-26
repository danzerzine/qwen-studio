# Как обновить ComfyUI

[← к README](../README.md) · [English version ↓](#updating-comfyui)

Qwen Studio только запускает и останавливает ComfyUI, а обновляется ComfyUI своими средствами.
Новые модели картинок (свежие версии Qwen-Image, FLUX и других) часто требуют свежего ComfyUI, поэтому
если шаблон пишет о неизвестных узлах или модель не загружается, начните с обновления.

## Перед обновлением

1. **Остановите ComfyUI в Studio**: в строке «Картинки» на вкладке «Обзор» нажмите квадратную кнопку
   «Остановить ComfyUI». Обновлять запущенный ComfyUI нельзя: файлы заняты.
2. **Сделайте копию того, что жалко потерять.** Модели обновление не трогает, но на всякий случай
   сохраните:
   - `ComfyUI\user\` — ваши сохранённые процессы (workflows) и настройки;
   - `ComfyUI\custom_nodes\` — установленные дополнения;
   - `ComfyUI\extra_model_paths.yaml`, если вы его создавали.

   Для портативной версии проще всего скопировать всю папку `ComfyUI_windows_portable\` без `models\`.

## Портативная версия

В папке `ComfyUI_windows_portable\update\` лежат три файла. Запускайте их двойным щелчком:

| Файл | Что делает | Когда запускать |
|---|---|---|
| `update_comfyui.bat` | обновляет ComfyUI до последней версии из репозитория | обычное обновление, нужны самые свежие узлы и модели |
| `update_comfyui_stable.bat` | обновляет до последнего стабильного релиза | если важнее надёжность, чем новинки |
| `update_comfyui_and_python_dependencies.bat` | обновляет ComfyUI **и** все Python-библиотеки, включая PyTorch | только если после обычного обновления ComfyUI пишет о недостающих или устаревших пакетах |

Третий вариант самый тяжёлый: он перекачивает несколько гигабайт и может сломать дополнения, которые
рассчитаны на старые версии библиотек. Поэтому сначала пробуйте первый или второй.

## Git-установка

Если ComfyUI установлен через `git clone` с виртуальным окружением (`venv` или `.venv`), выполните в папке
ComfyUI:

```bat
git pull
venv\Scripts\python.exe -m pip install -r requirements.txt
```

Для `.venv` замените `venv` на `.venv`. Вторая команда подтягивает новые зависимости, в том числе
обновлённый интерфейс ComfyUI, который ставится отдельным пакетом.

## Дополнения (custom nodes)

Обновление ComfyUI не обновляет дополнения. Если у вас установлен
[ComfyUI-Manager](https://github.com/Comfy-Org/ComfyUI-Manager), откройте его в интерфейсе ComfyUI
(кнопка Manager) и нажмите «Update All». Без него зайдите в каждую папку внутри `custom_nodes\`
и выполните `git pull`.

## После обновления

1. В Studio нажмите «Запустить» в строке «Картинки». Когда ComfyUI будет готов, Studio откроет его
   в браузере.
2. Если строка так и остаётся в состоянии запуска или ComfyUI сразу закрывается, откройте журнал
   `logs\studio\comfyui.log` в папке Studio. В конце будет ошибка: чаще всего это дополнение, несовместимое
   с новой версией. Временно перенесите его папку из `custom_nodes\` в другое место и запустите снова.
3. Если ничего не помогает, верните сохранённую копию или запустите `update_comfyui_stable.bat`.

---

# Updating ComfyUI

[← back to README](../README.md) · [Русская версия ↑](#как-обновить-comfyui)

Qwen Studio only starts and stops ComfyUI; ComfyUI updates itself with its own tools. New image models
(new Qwen-Image, FLUX and other releases) often need a recent ComfyUI, so if a template complains about
unknown nodes or a model fails to load, update first.

## Before updating

1. **Stop ComfyUI in Studio**: in the *Картинки* (Images) row on the *Обзор* (Overview) tab, press the square
   *Остановить ComfyUI* (Stop ComfyUI) button. A running ComfyUI cannot be updated: its files are in use.
2. **Back up what you would hate to lose.** Updates do not touch models, but to be safe copy:
   - `ComfyUI\user\` — your saved workflows and settings;
   - `ComfyUI\custom_nodes\` — installed extensions;
   - `ComfyUI\extra_model_paths.yaml`, if you created one.

   For the portable version the simplest way is to copy the whole `ComfyUI_windows_portable\` folder
   except `models\`.

## Portable version

The `ComfyUI_windows_portable\update\` folder has three files. Run them with a double click:

| File | What it does | When to run it |
|---|---|---|
| `update_comfyui.bat` | updates ComfyUI to the latest code from the repository | a regular update; you want the newest nodes and models |
| `update_comfyui_stable.bat` | updates to the latest stable release | when reliability matters more than new features |
| `update_comfyui_and_python_dependencies.bat` | updates ComfyUI **and** every Python library, PyTorch included | only if ComfyUI reports missing or outdated packages after a regular update |

The third one is the heaviest: it downloads several gigabytes and can break extensions that expect older
library versions. Try the first or the second one first.

## Git install

If ComfyUI was installed with `git clone` and a virtual environment (`venv` or `.venv`), run this in the
ComfyUI folder:

```bat
git pull
venv\Scripts\python.exe -m pip install -r requirements.txt
```

For `.venv`, replace `venv` with `.venv`. The second command pulls in new dependencies, including the
updated ComfyUI interface, which ships as a separate package.

## Extensions (custom nodes)

Updating ComfyUI does not update extensions. If you have
[ComfyUI-Manager](https://github.com/Comfy-Org/ComfyUI-Manager), open it in the ComfyUI interface
(the Manager button) and press *Update All*. Without it, run `git pull` in each folder inside
`custom_nodes\`.

## After updating

1. In Studio, press *Запустить* (Start) in the *Картинки* (Images) row. When ComfyUI is ready, Studio opens it
   in the browser.
2. If the row stays in the starting state or ComfyUI exits right away, open `logs\studio\comfyui.log` in the
   Studio folder. The error is at the end; most often it is an extension that does not work with the new
   version. Move its folder out of `custom_nodes\` for now and start again.
3. If nothing helps, restore your backup or run `update_comfyui_stable.bat`.
