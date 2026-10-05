using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using QwenStudio.Core;
using IOPath = System.IO.Path;
using Activity = QwenStudio.Core.Activity;

namespace QwenStudio
{
    public partial class MainWindow : Window
    {
        readonly Settings cfg = new();
        readonly ServerManager srv = new();
        readonly Gpu gpu = new();
        readonly Activity activity = new();
        readonly Ledger ledger = new();
        readonly WebUi webui;
        readonly Images images;
        bool imagesBusy;
        List<Profile> profiles = new();
        Profile selected;

        readonly BulkCollection<LogLine> log = new();
        readonly ObservableCollection<LogLine> events = new();
        readonly ConcurrentQueue<LogLine> incoming = new();
        ICollectionView logView;
        ScrollViewer logScroll;

        readonly DispatcherTimer drainTimer, pollTimer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<DateTime> cudaRestarts = new();
        readonly Dictionary<Profile, Ellipse> profileDots = new();
        bool polling, probing, keyShown, restarting, openWebWhenReady, closingForReal, warnedSlowStart, ledgerReady;
        int tick;
        /// <summary>Start/stop work in progress; a counter because Primary/Restart wrap Stop and StartProfile, which take it too.</summary>
        int busyN;
        bool busy => busyN > 0;
        long lastUiMs;
        GpuSample lastGpu = new();
        int webStatus;           // 0 off, 1 starting, 2 ready
        readonly bool autostart;

        // ledger input that arrived before the ledger finished loading
        readonly List<(DateTime at, int gen)> pendingReq = new();
        readonly List<DateTime> pendingRej = new();
        DateTime lastCudaAt, rejectNotedAt, lastArchive;
        int rejectsSinceNote;
        readonly SortedSet<string> rejectFrom = new();
        readonly ClientTracker clients = new();

        readonly EnvFile ui = new(IOPath.Combine(Paths.Logs, "ui.env"));

        /// <summary>Per-second GPU readings for the live chart (last LiveBarCount × LiveStep seconds).</summary>
        readonly Queue<(DateTime at, GpuSample g, double? tps)> liveSamples = new();
        const int LiveBarCount = 100, LiveStep = 3, LiveBridge = 2;
        bool liveMode;
        /// <summary>Average GPU load per live step (null = no reading) and the step under the mouse.</summary>
        readonly double?[] liveUtil = new double?[LiveBarCount];
        int? liveHover;

        /// <summary>Busy-guard window: a request finished this recently means a client (e.g. a batch run) is probably mid-loop.</summary>
        static readonly TimeSpan RecentWork = TimeSpan.FromMinutes(2);

        public MainWindow(bool autostart = false)
        {
            this.autostart = autostart;
            Theme.Apply(Theme.Parse(ui.Get("THEME")));
            L.Set(ui.Get("LANG"));
            InitializeComponent();
            L.Apply(this);
            (L.En ? LangEn : LangRu).IsChecked = true;
            if (File.Exists(Paths.OpencodeDesktop)) TxtOpencodeKind.Text = L.T("приложение для ПК");
            webui = new WebUi(cfg);
            images = new Images(cfg, gpu);
            Theme.Changed += OnThemeChanged;
            OnThemeChanged();

            logView = CollectionViewSource.GetDefaultView(log);
            LogList.ItemsSource = logView;
            EventsList.ItemsSource = events;

            srv.Line += l => incoming.Enqueue(l);
            srv.CudaError += () => Dispatcher.BeginInvoke(OnCudaError);
            srv.Exited += () => Dispatcher.BeginInvoke(OnExited);
            srv.Completed += (at, gen) => Dispatcher.BeginInvoke(() => OnCompleted(at, gen));
            srv.Rejected += at => Dispatcher.BeginInvoke(() => OnRejected(at));

            drainTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => DrainLog(), Dispatcher);
            pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, async (_, _) => await Poll(), Dispatcher);

            SourceInitialized += (_, _) => DarkTitleBar();
            Loaded += async (_, _) =>
            {
                model = Profile.ParseSlot(ui.Get("MODEL"));
                // the toggles ask the models whether they can reason: read both headers once, off the UI thread
                await Task.Run(() => { foreach (var k in new[] { "MODEL_PATH", "FALLBACK_MODEL_PATH", "UNCENSORED_MODEL_PATH" }) Gguf.Thinks(Paths.Resolve(cfg.Env.Get(k))); });
                LoadProfiles();
                srv.Attach(profiles, cfg.Port);
                if (srv.Profile != null && srv.Profile.Slot != model) { model = srv.Profile.Slot; LoadProfiles(); }
                ShowModels();
                (ui.Get("CHART") == "live" ? ChartLive : ChartDay).IsChecked = true;
                if (srv.Profile != null) SelectVariant(srv.Profile);
                FillSettings();
                if (srv.State == ServerState.Stopped) srv.Emit(L.F("Готово. Сервер остановлен · {0} {1} · {2}", profiles.Count, (profiles.Count % 10 is >= 2 and <= 4 && profiles.Count % 100 is < 12 or > 14 ? L.T("профиля") : profiles.Count % 10 == 1 && profiles.Count % 100 != 11 ? L.T("профиль") : L.T("профилей")), cfg.LanIp));
                UpdateUi();
                drainTimer.Start();
                pollTimer.Start();
                clients.Port = cfg.Port;
                clients.Start(System.Threading.CancellationToken.None);

                await LoadLedger();
                _ = ArchiveLogs(manual: false);
                if (autostart) _ = AutoStartServer();
            };
            Closing += OnClosing;
            Closed += (_, _) => { if (ledgerReady) ledger.Flush(); };
        }

        // ───────────────────────── ledger ─────────────────────────

        async Task LoadLedger()
        {
            var fallback = IOPath.GetFileName(cfg.Env.Get("FALLBACK_MODEL_PATH"));
            var uncensored = IOPath.GetFileName(cfg.Env.Get("UNCENSORED_MODEL_PATH"));
            var live = srv.Attached ? srv.LogFile : null;
            try { await Task.Run(() => ledger.Load(fallback, uncensored, live)); }
            catch (Exception e) { srv.Emit(L.T("Учёт работы не прочитан: ") + e.Message, LogKind.Warn); }
            ledgerReady = true;
            foreach (var (at, gen) in pendingReq) ledger.Request(at, gen);
            foreach (var at in pendingRej) ledger.Reject(at);
            pendingReq.Clear(); pendingRej.Clear();
            if (srv.Attached && ServerUp) ledger.Event("attach", srv.Profile, srv.Build, gpu.Driver, srv.Pid, srv.Since != default ? (DateTime.Now - srv.Since).TotalMinutes : 0);
            int rej = ledger.RejectedSince(DateTime.Now.AddHours(-24));
            if (rej > 0)
                srv.Emit(L.F("За сутки сервер отклонил {0} запрос(ов) с неверным API-ключом — у какого-то клиента старый ключ.", rej), LogKind.Warn);
            RenderChart();
            UpdateUi();
        }

        void OnCompleted(DateTime at, int gen)
        {
            if (ledgerReady) ledger.Request(at, gen); else pendingReq.Add((at, gen));
        }

        async void OnRejected(DateTime at)
        {
            if (ledgerReady) ledger.Reject(at); else pendingRej.Add(at);
            rejectsSinceNote++;
            // the client tracker sees the connection within 250 ms; give it that time before asking who it was
            var seenAt = DateTime.Now;
            await Task.Delay(600);
            var who = clients.Blame(seenAt);
            if (who != null) rejectFrom.Add(who.Title);
            // a client with a stale key retries a lot: one event per 30 minutes is enough
            if (DateTime.Now - rejectNotedAt < TimeSpan.FromMinutes(30)) return;
            var from = rejectFrom.Count > 0 ? L.T(" от ") + string.Join(", ", rejectFrom) : "";
            srv.Emit(L.F("Запрос с неверным API-ключом отклонён{0} ({1} шт. с прошлого сообщения). У клиента старый ключ — выдайте ему текущий (Настройки → API-ключ).", from, rejectsSinceNote), LogKind.Warn);
            rejectNotedAt = DateTime.Now;
            rejectsSinceNote = 0;
            rejectFrom.Clear();
        }

        void OnExited()
        {
            if (srv.State == ServerState.Crashed && DateTime.Now - lastCudaAt > TimeSpan.FromMinutes(1))
                LedgerEvent("crash", srv.Profile, srv.ExitInfo);
            UpdateUi();
        }

        void LedgerEvent(string ev, Profile p, string reason = null)
        {
            if (!ledgerReady) return;
            bool up = srv.Since != default && ev is "stop" or "crash" or "cuda";
            ledger.Event(ev, p, srv.Build, gpu.Driver, srv.Pid,
                         up ? (DateTime.Now - srv.Since).TotalMinutes : 0,
                         up ? ledger.RequestsSince(srv.Since) : 0, reason);
        }

        // ───────────────────────── profiles ─────────────────────────

        void LoadProfiles()
        {
            try { profiles = Profile.LoadAll(); }
            catch (Exception e) { srv.Emit(L.T("profiles.json не читается: ") + e.Message, LogKind.Error); profiles = new(); }

            ProfilesPanel.Children.Clear();
            profileDots.Clear();
            profileBadges.Clear();
            foreach (var p in profiles)
            {
                var dot = new Ellipse { Style = R<Style>("Dot"), Margin = R<Thickness>("GapRightS"), Visibility = Visibility.Collapsed };
                dot.SetResourceReference(Shape.FillProperty, "Good");
                profileDots[p] = dot;
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(dot);
                head.Children.Add(new TextBlock { Text = L.T(p.Name), Style = R<Style>("BodyStrong") });
                var body = new StackPanel();
                body.Children.Add(head);
                var badge = new TextBlock { Style = R<Style>("Label"), TextWrapping = TextWrapping.Wrap };
                profileBadges[p] = badge;
                body.Children.Add(badge);
                var rb = new RadioButton { Style = R<Style>("ProfileCard"), GroupName = "profile", Content = body, Tag = p };
                if (!string.IsNullOrWhiteSpace(p.Description)) rb.ToolTip = L.T(p.Description);
                rb.Checked += (s, _) => { selected = (Profile)((RadioButton)s).Tag; SaveUiState(); ShowOptions(); UpdateUi(); };
                ProfilesPanel.Children.Add(rb);
            }

            var last = ui.Get("PROFILE");
            var same = profiles.FirstOrDefault(p => p.Id == last);
            if (same != null) Select(same);
            else if (Profile.Resolve(profiles, last, model) is Profile former) SelectVariant(former);   // «Без размышлений» → «Чат» with thinking off
            else Select(profiles.FirstOrDefault());
            ShowOptions();
            ProfilesText.Text = L.F("profiles.json · {0} шт.", profiles.Count);
        }

        void Select(Profile p)
        {
            if (p == null) return;
            foreach (RadioButton rb in ProfilesPanel.Children)
                if (((Profile)rb.Tag).Id == p.Id) rb.IsChecked = true;
        }

        /// <summary>Selects a launch variant's mode and sets that mode's toggles to match it (the running server, a former card).</summary>
        void SelectVariant(Profile v)
        {
            var p = profiles.FirstOrDefault(x => x.Id == v.Id);
            if (p == null) return;
            try
            {
                if (SeeBlock(p) == null) ui.Set("VISION_" + p.Id, v.Sees ? "1" : "0");
                if (ThinkBlock() == null) ui.Set("THINK_" + p.Id, v.Thinks ? "1" : "0");
            }
            catch { }
            Select(p);
            ShowOptions();
        }

        /// <summary>The model switch: Profile.MainSlot, OldSlot or UncensoredSlot.</summary>
        string model = Profile.MainSlot;
        bool showingOptions;
        readonly Dictionary<Profile, TextBlock> profileBadges = new();

        /// <summary>The selected mode on the selected model with its toggles — what Start will launch.</summary>
        Profile Chosen => selected == null ? null : VariantOf(selected);

        /// <summary>A mode on the selected model with its remembered toggles; what the mode or the model cannot do is off.</summary>
        Profile VariantOf(Profile p) =>
            p.Variant(model, SeeBlock(p) == null && Wants(p, "VISION", p.VisionByDefault), ThinkBlock() == null && Wants(p, "THINK", p.ThinksByDefault));

        bool Wants(Profile p, string option, bool byDefault) => ui.Get(option + "_" + p.Id) switch { "1" => true, "0" => false, _ => byDefault };

        /// <summary>Why the mode cannot see on this model, or null when it can.</summary>
        string SeeBlock(Profile p)
        {
            if (string.IsNullOrEmpty(p.Mmproj))
                return L.F("В режиме «{0}» зрение не предусмотрено: модулю зрения (~1 ГБ) не хватит видеопамяти рядом с его контекстом.", L.T(p.Name));
            var file = cfg.MmprojFile(p);
            return File.Exists(file) ? null : L.F("Не найден модуль зрения:\n{0}\n\nУкажите путь в «Настройки → Сервер и модели».", file);
        }

        /// <summary>Why the selected model cannot reason, or null when it can (or it is not known: the model file is missing).</summary>
        string ThinkBlock()
        {
            var file = Paths.Resolve(cfg.Env.Get(Profile.ModelKeyOf(model)));
            return Gguf.Thinks(file) == false
                ? L.F("Модель {0} не умеет размышлять: в её шаблоне чата нет режима размышлений.", IOPath.GetFileName(file))
                : null;
        }

        /// <summary>Puts the selected mode's toggles and every card's badge in line with the remembered options.</summary>
        void ShowOptions()
        {
            foreach (var kv in profileBadges) kv.Value.Text = VariantOf(kv.Key).Badge;
            if (selected == null) return;
            showingOptions = true;
            var see = SeeBlock(selected);
            VisionToggle.IsEnabled = see == null;
            VisionToggle.IsChecked = see == null && Wants(selected, "VISION", selected.VisionByDefault);
            VisionToggle.ToolTip = see ?? L.T("Модель понимает картинки: скриншоты, документы, фото. Модуль зрения занимает ещё ~1 ГБ видеопамяти.");
            var think = ThinkBlock();
            ThinkToggle.IsEnabled = think == null;
            ThinkToggle.IsChecked = think == null && Wants(selected, "THINK", selected.ThinksByDefault);
            ThinkToggle.ToolTip = think ?? L.T("Модель размышляет перед ответом: точнее на сложных задачах, но отвечает дольше. Без размышлений — ответ сразу: перевод, короткие ответы, пакетные прогоны.");
            showingOptions = false;
        }

        void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (showingOptions || selected == null || !IsLoaded) return;
            var box = (CheckBox)sender;
            try { ui.Set((box == VisionToggle ? "VISION_" : "THINK_") + selected.Id, box.IsChecked == true ? "1" : "0"); } catch { }
            ShowOptions();
            UpdateUi();
        }

        void Model_Checked(object sender, RoutedEventArgs e)
        {
            var slot = sender == ModelOld ? Profile.OldSlot : sender == ModelUncensored ? Profile.UncensoredSlot : Profile.MainSlot;
            if (slot == model || !IsLoaded || showingModels) return;
            model = slot;
            try { ui.Set("MODEL", slot); } catch { }
            LoadProfiles();                    // badges change: no MTP, 96K cap
            UpdateUi();
        }

        bool showingModels;

        /// <summary>
        /// Model switch: checks the current slot, file names in the tooltips. The uncensored tab is off until its file is set
        /// in Settings (the reason is in its tooltip); a server running on it keeps the tab on regardless.
        /// </summary>
        void ShowModels()
        {
            showingModels = true;
            ModelMain.ToolTip = IOPath.GetFileName(cfg.Env.Get("MODEL_PATH"));
            ModelOld.ToolTip = IOPath.GetFileName(cfg.Env.Get("FALLBACK_MODEL_PATH")) + L.T(" — запасная модель, без MTP, контекст до 96K");
            // the fallback switch only makes sense when a second model is configured
            ModelOld.IsEnabled = model == Profile.OldSlot || cfg.Env.Get("FALLBACK_MODEL_PATH") != "";
            var path = cfg.Env.Get("UNCENSORED_MODEL_PATH");
            bool has = path != "" && File.Exists(Paths.Resolve(path));
            bool runs = srv.Profile?.Slot == Profile.UncensoredSlot && ServerUp;
            ModelUncensored.IsEnabled = has || runs;
            ModelUncensored.ToolTip = has
                ? IOPath.GetFileName(path) + L.T(" — основная модель, дообученная без отказов: тот же сервер, MTP и режимы")
                : path == "" ? L.T("Модель без цензуры не выбрана: укажите файл в «Настройки → Сервер и модели».")
                : L.F("Не найден файл модели без цензуры:\n{0}\n\nУкажите путь в «Настройки → Сервер и модели».", Paths.Resolve(path));
            if (!ModelUncensored.IsEnabled && model == Profile.UncensoredSlot) { model = Profile.MainSlot; LoadProfiles(); }
            (model == Profile.OldSlot ? ModelOld : model == Profile.UncensoredSlot ? ModelUncensored : ModelMain).IsChecked = true;
            showingModels = false;
        }

        void SaveUiState() { try { if (selected != null && ui.Get("PROFILE") != selected.Id) ui.Set("PROFILE", selected.Id); } catch { } }

        // ───────────────────────── start / stop ─────────────────────────

        bool ServerUp => srv.State is ServerState.Starting or ServerState.Running or ServerState.External;

        /// <summary>The running server is exactly what is selected in the sidebar.</summary>
        bool SelectionRuns => srv.Profile != null && selected != null && srv.State != ServerState.External &&
                              srv.Profile.Key == Chosen.Key;

        async void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (busy || selected == null) return;
            busyN++; UpdateUi();
            try
            {
                if (!ServerUp) { await StartProfile(Chosen, checks: true); return; }
                // switch to the selected mode
                if (srv.State == ServerState.External &&
                    !Ask(L.F("На порту {0} работает llama-server, запущенный не из Qwen Studio. Остановить его и запустить «{1}»?", cfg.Port, Chosen.Title))) return;
                if (!await ConfirmInterrupt(L.F("Переключить сервер на «{0}»", Chosen.Title))) return;
                await Stop("switch");
                await StartProfile(Chosen, checks: false);
            }
            finally { busyN--; UpdateUi(); }
        }

        async void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (busy || !ServerUp) return;
            busyN++; UpdateUi();
            try
            {
                if (!await ConfirmInterrupt(L.T("Остановить сервер"))) return;
                await Stop("user");
            }
            finally { busyN--; UpdateUi(); }
        }

        async void Restart_Click(object sender, RoutedEventArgs e)
        {
            if (busy || srv.Profile == null) return;
            busyN++; UpdateUi();
            try
            {
                if (!await ConfirmInterrupt(L.T("Перезапустить сервер"))) return;
                var p = srv.Profile;
                if (p == null) return;
                await Stop("restart");
                await StartProfile(p, checks: false);
            }
            finally { busyN--; UpdateUi(); }
        }

        /// <summary>
        /// The busy guard: stopping mid-request cuts off whoever is being served — often someone's batch run.
        /// Asks when a slot is busy right now or a request finished within the last two minutes.
        /// </summary>
        async Task<bool> ConfirmInterrupt(string action)
        {
            if (!ServerUp || srv.State == ServerState.Starting) return true;
            int port = srv.Port > 0 ? srv.Port : cfg.Port;
            await activity.Poll(port, cfg.ApiKey);
            if (activity.Busy > 0)
                return Ask(L.F("Сервер сейчас обрабатывает запросы: занято {0} из {1} слотов. Они оборвутся на середине.\n\n{2}?", activity.Busy, activity.Slots, action));
            var last = new[] { activity.LastBusy, srv.LastDone }.Max();
            if (last is DateTime t && DateTime.Now - t < RecentWork)
                return Ask(L.F("Последний запрос закончился {0} назад — клиент (например, пакетный прогон) может прислать следующий в любую секунду.\n\n{1}?", Ago(DateTime.Now - t), action));
            return true;
        }

        async Task Stop(string reason)
        {
            busyN++; UpdateUi();
            try
            {
                LedgerEvent("stop", srv.Profile, reason);
                srv.Emit(L.T("Останавливаю сервер…"));
                await srv.Stop();
                activity.Reset();
                srv.Emit(L.T("Сервер остановлен, видеопамять освобождена."));
            }
            finally { busyN--; UpdateUi(); }
        }

        /// <summary>auto: launched by autostart — no dialogs; any obstacle becomes an event and the start is skipped.</summary>
        async Task StartProfile(Profile p, bool checks, bool auto = false)
        {
            busyN++; UpdateUi();
            void Fail(string text)
            {
                if (auto) { srv.Emit(L.T("Автозапуск пропущен: ") + text.Replace("\n\n", " "), LogKind.Warn); LedgerEvent("autostart_skip", p, text); }
                else Error(text);
            }
            try
            {
                cfg.Env.Load();
                string exe = cfg.ServerExe(p), model = cfg.ModelFile(p);
                if (!File.Exists(exe)) { Fail(L.F("Не найден llama-server:\n{0}\n\nУкажите путь в «Настройки → Сервер и модели».", exe)); return; }
                if (!File.Exists(model)) { Fail(L.F("Не найдена модель:\n{0}\n\nУкажите путь в «Настройки → Сервер и модели».", model)); return; }
                string mmproj = cfg.MmprojFile(p);
                if (mmproj != null && !File.Exists(mmproj)) { Fail(L.F("Не найден модуль зрения:\n{0}\n\nУкажите путь в «Настройки → Сервер и модели».", mmproj)); return; }
                if (cfg.OnLan && string.IsNullOrEmpty(cfg.ApiKey))
                { Fail(L.T("Сервер открыт для локальной сети, но API-ключ не задан — моделью мог бы пользоваться любой в сети.\n\nСоздайте ключ: «Настройки → Новый ключ…».")); return; }

                var ollama = await Neighbours.OllamaModels();
                if (ollama.Count > 0)
                {
                    if (auto) { Fail(L.F("Ollama держит в видеопамяти: {0}", string.Join(", ", ollama))); return; }
                    if (!Ask(L.F("Ollama держит в видеопамяти: {0}.\n\nДве модели на одной карте мешают друг другу. Выгрузить их из Ollama и продолжить?", string.Join(", ", ollama)))) return;
                    srv.Emit(L.T("Выгружаю модели Ollama…"));
                    await Neighbours.UnloadOllama(ollama);
                }
                if (await Neighbours.LmStudioBusy())
                {
                    if (auto) { Fail(L.T("запущен сервер LM Studio")); return; }
                    if (!Ask(L.T("Запущен сервер LM Studio — его модели тоже могут занимать видеопамять.\n\nВыгрузить модели LM Studio и продолжить?"))) return;
                    srv.Emit(L.T("Выгружаю модели LM Studio…"));
                    await Neighbours.UnloadLmStudio();
                }
                if (images.Installed)
                {
                    await images.Probe();
                    if (images.Generating) { Fail(L.T("ComfyUI сейчас рисует картинку.\n\nДождитесь конца генерации и запустите сервер снова.")); return; }
                    // card-wide memory: only ComfyUI's while our server is down (a running server gets stopped below anyway)
                    if (!ServerUp && images.HeldMiB >= Images.HeldThresholdMiB)
                    {
                        if (auto) { Fail(L.F("ComfyUI держит в видеопамяти модели картинок ({0:0.0} ГБ)", images.HeldMiB / 1024.0)); return; }
                        if (!Ask(L.F("ComfyUI держит в видеопамяти модели картинок: {0:0.0} ГБ.\n\nВыгрузить их и продолжить? Картинки останутся запущены — модель загрузится заново при следующей генерации, и тогда сервер Qwen нужно будет остановить.", images.HeldMiB / 1024.0))) return;
                        srv.Emit(L.T("Выгружаю модели ComfyUI из видеопамяти…"));
                        await images.FreeVram();
                    }
                }
                if (ServerUp)
                {
                    if (auto) { Fail(L.T("сервер уже работает")); return; }
                    if (srv.State == ServerState.External && !Ask(L.F("На порту {0} уже работает llama-server, запущенный не отсюда. Остановить его и запустить выбранный профиль?", cfg.Port))) return;
                    LedgerEvent("stop", srv.Profile, "switch");
                    await srv.Stop();
                }
                var owner = ServerManager.PortOwnerName(cfg.Port);
                if (owner != null) { Fail(L.F("Порт {0} занят программой «{1}».\n\nОсвободите его или смените порт в «Настройки → Сеть».", cfg.Port, owner)); return; }
                if (checks || auto)
                {
                    await Task.Delay(300);
                    var g = await Task.Run(gpu.Read);
                    if (g.Ok && g.UsedMiB > 1500)
                    {
                        if (auto) { Fail(L.F("на видеокарте уже занято {0:0.0} ГБ другими программами", g.UsedMiB / 1024)); return; }
                        if (!Ask(L.F("На видеокарте уже занято {0:0.0} ГБ другими программами, а модели нужно около 15 ГБ.\n\nВсё равно запустить?", g.UsedMiB / 1024))) return;
                    }
                }

                warnedSlowStart = false;
                activity.Reset();
                srv.Start(p, cfg);
                LedgerEvent("start", p, auto ? "autostart" : null);
                try { ui.Set("LAST_START", p.Key); } catch { }
            }
            catch (Exception ex) { Fail(ex.Message); }
            finally { busyN--; UpdateUi(); }
        }

        async void OnCudaError()
        {
            if (restarting || busy || srv.Profile == null) return;
            restarting = true;
            busyN++; UpdateUi();
            lastCudaAt = DateTime.Now;
            try
            {
                var p = srv.Profile;
                LedgerEvent("cuda", p, "CUDA error");
                cudaRestarts.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(10));
                if (cudaRestarts.Count >= 2)
                {
                    srv.Emit(L.T("Снова ошибка CUDA — автоперезапуск отключён. Проверьте журнал; возможна нестабильность видеокарты."), LogKind.Error);
                    return;
                }
                cudaRestarts.Add(DateTime.Now);
                srv.Emit(L.T("Ошибка CUDA — перезапускаю сервер через 3 секунды…"), LogKind.Warn);
                await srv.Stop();
                await Task.Delay(3000);
                await StartProfile(p, checks: false);
            }
            finally { restarting = false; busyN--; UpdateUi(); }
        }

        /// <summary>
        /// Started by Windows with --autostart and allowed to bring the server up: wait for the system to settle,
        /// then start the last started mode — only if the card is free and nobody else serves on the port.
        /// </summary>
        async Task AutoStartServer()
        {
            if (ui.Get("AUTOSTART_SERVER") != "1") return;
            var p = Profile.FromKey(profiles, ui.Get("LAST_START"));
            if (p == null) { srv.Emit(L.T("Автозапуск: сервер ещё ни разу не запускался из Qwen Studio — нечего поднимать."), LogKind.Warn); return; }
            if (ServerUp) { srv.Emit(L.T("Автозапуск: сервер уже работает.")); return; }
            srv.Emit(L.F("Автозапуск: через 20 с подниму «{0}», если видеокарта свободна.", p.Title));
            await Task.Delay(TimeSpan.FromSeconds(20));
            for (int i = 0; i < 24 && !(await Task.Run(gpu.Read)).Ok; i++) await Task.Delay(TimeSpan.FromSeconds(5));
            if (ServerUp || busy) { srv.Emit(L.T("Автозапуск отменён: сервер уже запущен.")); return; }
            await StartProfile(p, checks: false, auto: true);
        }

        // ───────────────────────── polling ─────────────────────────

        async Task Poll()
        {
            if (polling) return;
            polling = true;
            tick++;
            try
            {
                lastGpu = await Task.Run(gpu.Read);
                RecordLive();
                if (!probing) Probe(tick);

                if (ledgerReady)
                {
                    bool up = srv.State is ServerState.Running or ServerState.External;
                    ledger.Tick(DateTime.Now, up, srv.Profile, activity.Busy, activity.Slots, lastGpu.Ok ? (int)lastGpu.UsedMiB : 0);
                    if (DateTime.Now.Second == 0 || tick % 60 == 0)
                    {
                        RenderChart();
                        if (PageStats.Visibility == Visibility.Visible) RenderStats();
                    }
                }
                if (tick % 3600 == 0 && DateTime.Now - lastArchive > TimeSpan.FromHours(23)) _ = ArchiveLogs(manual: false);
            }
            finally
            {
                polling = false;
                UpdateUi();
            }
        }

        /// <summary>Health, slots, attach, ComfyUI and Open WebUI probes: a slow answer delays only the next probe, not sampling.</summary>
        async void Probe(int tick)
        {
            probing = true;
            try
            {
                clients.Port = srv.Port > 0 && ServerUp ? srv.Port : cfg.Port;
                if (ServerUp)
                {
                    int port = srv.Port > 0 ? srv.Port : cfg.Port;
                    int code = await Http.Status($"http://127.0.0.1:{port}/health");
                    if (code == 200 && srv.State == ServerState.Starting)
                    {
                        srv.MarkRunning();
                        if (srv.Attached)
                            srv.Emit(L.F("Сервер отвечает · работает уже {0} · {1}:{2}", Elapsed(DateTime.Now - srv.Since), cfg.LanIp, port), LogKind.Good);
                        else
                        {
                            srv.Emit(L.F("Готов к работе за {0} · {1}:{2}", Elapsed(DateTime.Now - srv.Since), cfg.LanIp, port), LogKind.Good);
                            LedgerEvent("ready", srv.Profile, $"load {(DateTime.Now - srv.Since).TotalSeconds:0}s");
                        }
                    }
                    if (srv.State == ServerState.Starting && !warnedSlowStart && DateTime.Now - srv.Since > TimeSpan.FromMinutes(10))
                    {
                        warnedSlowStart = true;
                        srv.Emit(L.T("Сервер не отвечает уже 10 минут — проверьте журнал."), LogKind.Warn);
                    }
                    if (code == 200 && srv.State is ServerState.Running or ServerState.External)
                        await activity.Poll(port, cfg.ApiKey);
                    if (srv.State == ServerState.External && tick % 2 == 0 && !busy && Net.PortOwner(port) == 0)
                    {
                        await srv.Stop();
                        activity.Reset();
                    }
                }
                else
                {
                    if (activity.Ok) activity.Reset();
                    if (tick % 3 == 0 && !busy && Net.PortOwner(cfg.Port) != 0) srv.Attach(profiles, cfg.Port);
                }

                if (tick % 3 == 1 && images.Installed && !imagesBusy) await images.Probe();

                if (tick % 2 == 0 || openWebWhenReady)
                {
                    int w = await Http.Status(webui.Url + "/health");
                    webStatus = w == 200 ? 2 : webui.Running ? 1 : 0;
                    if (webStatus == 2 && openWebWhenReady) { openWebWhenReady = false; Proc.Open(webui.Url); }
                    if (webStatus == 0 && openWebWhenReady && clock.Elapsed.TotalSeconds > webStartAt + 20)
                    {
                        openWebWhenReady = false;
                        srv.Emit(L.T("Open WebUI не запустился — см. журнал ") + webui.LogFile, LogKind.Error);
                    }
                }
            }
            finally
            {
                probing = false;
                UpdateUi();
            }
        }

        // ───────────────────────── UI refresh ─────────────────────────

        /// <summary>Connection card «Клиенты»: who has a connection open now, else who was last; the tooltip lists 24 hours.</summary>
        void UpdateClients()
        {
            var all = clients.All();
            var open = all.Where(c => c.Open).ToList();
            var now = DateTime.Now;
            ClientsText.Text = open.Count > 0 ? string.Join(", ", open.Select(c => c.Short))
                : all.Count > 0 ? L.F("сейчас нет · {0} — {1} назад", all[0].Short, Ago(now - all[0].LastSeen))
                : L.T("пока никого");
            Paint(ClientsText, TextBlock.ForegroundProperty, open.Count > 0 ? "Text" : "Muted");
            Paint(ClientsDot, Shape.FillProperty, all.Any(c => c.Rejected > 0) ? "Warn" : open.Count > 0 ? "Good" : "Faint");
            if (all.Count == 0) { ClientsRow.ToolTip = L.T("Адреса программ, которые обращались к серверу модели, — за последние сутки."); return; }
            var tip = new StringBuilder(L.T("За сутки (с запуска Qwen Studio):"));
            foreach (var c in all)
            {
                tip.Append(L.F("\n{0} — {1}, впервые в {2:HH:mm}", c.Title, (c.Open ? L.T("подключён сейчас") : Ago(now - c.LastSeen) + L.T(" назад")), c.FirstSeen));
                if (c.Rejected > 0) tip.Append(L.F(" · неверный ключ: {0}", c.Rejected));
            }
            ClientsRow.ToolTip = tip.ToString();
        }

        void UpdateUi()
        {
            var st = srv.State;
            var running = srv.Profile;
            var now = DateTime.Now;

            // hero: state
            (string text, string brush, string sub) s = st switch
            {
                ServerState.Starting => (L.T("Загружается…"), "Warn", $"{running?.Title ?? L.T("профиль")} · {Elapsed(now - srv.Since)}"),
                ServerState.Running => (L.T("Работает"), "Good", string.Join(" · ", new[] { running?.ModelTitle, running?.Badge, srv.Build, L.T("работает ") + Elapsed(now - srv.Since) }.Where(x => !string.IsNullOrEmpty(x)))),
                ServerState.External => (L.T("Работает (внешний)"), "Warn", L.F("llama-server PID {0} запущен не из Qwen Studio", srv.Pid) + (srv.Build != null ? " · " + srv.Build : "")),
                ServerState.Crashed => (L.T("Упал"), "Bad", L.F("{0} · {1} · подробности во вкладке «Журнал»", running?.Title, srv.ExitInfo)),
                _ => (L.T("Остановлен"), "Muted", L.T("выберите режим слева и запустите")),
            };
            if (busy && !ServerUp) s = (L.T("Подготовка…"), "Warn", L.T("проверяю видеокарту и соседей"));
            StateText.Text = s.text;
            Paint(StateText, TextBlock.ForegroundProperty, s.brush);
            StateSub.Text = s.sub;
            StateSub.ToolTip = running != null ? IOPath.GetFileName(cfg.ModelFile(running)) : null;
            Paint(PillDot, Shape.FillProperty, s.brush == "Muted" ? "Faint" : s.brush);
            PillText.Text = st switch
            {
                ServerState.Running => L.F("Работает · {0}", running?.Title),
                ServerState.Starting => L.T("Загружается…"),
                ServerState.External => L.T("Работает (внешний)"),
                ServerState.Crashed => L.T("Сервер упал"),
                _ => L.T("Сервер остановлен"),
            };

            // activity chip
            bool serving = st is ServerState.Running or ServerState.External;
            ActivityChip.Visibility = serving ? Visibility.Visible : Visibility.Collapsed;
            if (serving)
            {
                var lastWork = new[] { activity.LastBusy, srv.LastDone }.Max();
                if (!activity.Ok)
                {
                    ActivityText.Text = L.T("нет данных о слотах");
                    Paint(ActivityChip, Border.BackgroundProperty, "Surface2");
                    Paint(ActivityDot, Shape.FillProperty, "Faint");
                }
                else if (activity.Busy > 0)
                {
                    ActivityText.Text = L.F("Занято {0} из {1}", activity.Busy, activity.Slots);
                    Paint(ActivityChip, Border.BackgroundProperty, "AccentSoft");
                    Paint(ActivityDot, Shape.FillProperty, "Accent");
                }
                else
                {
                    ActivityText.Text = lastWork is DateTime lw ? L.F("Свободен · последний запрос {0} назад", Ago(now - lw)) : L.F("Свободен · {0} слот(а)", activity.Slots);
                    Paint(ActivityChip, Border.BackgroundProperty, "Surface2");
                    Paint(ActivityDot, Shape.FillProperty, lastWork is DateTime r && now - r < RecentWork ? "Warn" : "Good");
                }
                ActivityChip.ToolTip = lastWork is DateTime t2 && now - t2 < RecentWork && activity.Busy == 0
                    ? L.T("Запрос закончился меньше 2 минут назад — остановка и перезапуск спросят подтверждение") : null;
            }

            // metrics
            AggTps.Text = activity.AggTps is double a ? L.F("{0:0.0} т/с", a) : serving && activity.Ok ? L.T("0 т/с") : "—";
            AggSub.Text = activity.Busy > 0 ? L.F("{0} из {1} слотов генерируют", activity.Busy, activity.Slots) : L.T("все слоты вместе");
            GenTps.Text = srv.GenTps is double g ? L.F("{0:0.0} т/с", g) : "—";
            var parts = new List<string>();
            if (srv.PromptTps is double pp) parts.Add(L.F("промпт {0:0} т/с", pp));
            if (srv.DraftAccept is double d) parts.Add($"MTP {(d <= 1 ? d * 100 : d):0}%");
            else if (running?.Mtp == false) parts.Add(L.T("MTP выкл"));
            LastSub.Text = parts.Count > 0 ? string.Join(" · ", parts) : L.T("запросов ещё не было");
            if (ledgerReady)
            {
                ReqHour.Text = ledger.RequestsSince(now.AddHours(-1)).ToString("N0");
                var sub = L.F("за сутки {0:N0}", ledger.RequestsSince(now.AddHours(-24)));
                if (ServerUp && srv.Since != default) sub += L.F(" · сессия {0:N0}", ledger.RequestsSince(srv.Since));
                ReqSub.Text = sub;
                int m = ledger.CleanMinutes;
                CleanText.Text = m >= 60 ? L.F("{0:0.#} ч", m / 60.0) : L.F("{0} мин", m);
                CleanSub.Text = L.F("{0:N0} запросов", ledger.CleanRequests) + (srv.Build != null ? " · " + srv.Build : "");
                CleanTile.ToolTip = ledger.CleanSince is DateTime cs
                    ? (ledger.HasFailures ? L.F("Часы работы и запросы с последнего сбоя ({0:dd.MM HH:mm})", cs) : L.F("Сбоев не было с начала учёта ({0:dd.MM HH:mm})", cs))
                    : L.T("Учёт только начался");
            }

            // actions: Primary starts or switches; restart and stop sit below it
            lastUiMs = clock.ElapsedMilliseconds;
            var chosen = Chosen;
            bool same = SelectionRuns;
            bool showPrimary = !ServerUp || !same;
            BtnPrimary.Visibility = showPrimary && st != ServerState.Starting ? Visibility.Visible : Visibility.Collapsed;
            BtnPrimary.IsEnabled = !busy && selected != null;
            if (busy) { BtnPrimaryIcon.Text = ""; BtnPrimaryText.Text = L.T("Подождите…"); }
            else if (!ServerUp) { BtnPrimaryIcon.Text = ""; BtnPrimaryText.Text = L.F("Запустить «{0}»", chosen?.ShortTitle); }
            else if (running != null && chosen != null && running.Id == chosen.Id && running.Slot == chosen.Slot && st != ServerState.External)
            { BtnPrimaryIcon.Text = ""; BtnPrimaryText.Text = L.T("Применить изменения"); }     // same mode, only the toggles differ
            else { BtnPrimaryIcon.Text = ""; BtnPrimaryText.Text = running != null && chosen != null && running.Id == chosen.Id
                ? chosen.Slot switch { Profile.OldSlot => L.T("Переключить на запасную модель"), Profile.UncensoredSlot => L.T("Переключить на модель без цензуры"), _ => L.T("Переключить на основную модель") }   // same mode, the model switch moved
                : L.F("Переключить на «{0}»", chosen?.ShortTitle); }
            BtnPrimary.ToolTip = chosen == null ? null : $"{chosen.Title}\n{chosen.Badge}";
            SecondaryActions.Visibility = ServerUp ? Visibility.Visible : Visibility.Collapsed;
            bool showRestart = ServerUp && same && st != ServerState.Starting;
            BtnRestart.Visibility = showRestart ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(BtnStop, showRestart ? 2 : 0);
            Grid.SetColumnSpan(BtnStop, showRestart ? 1 : 3);
            BtnRestart.IsEnabled = BtnStop.IsEnabled = !busy;
            string hint = st switch
            {
                ServerState.External => L.T("Этот llama-server запущен не из Qwen Studio: остановить его можно, перезапустить — нет."),
                ServerState.Crashed => L.T("Сервер упал. Что случилось — во вкладке «Журнал»; запустить заново можно кнопкой выше."),
                _ => null,
            };
            ActionHint.Text = hint ?? "";
            ActionHint.Visibility = hint != null ? Visibility.Visible : Visibility.Collapsed;
            foreach (var kv in profileDots)
                kv.Value.Visibility = running != null && kv.Key.Id == running.Id && running.Slot == model && st != ServerState.Crashed ? Visibility.Visible : Visibility.Collapsed;

            // gpu
            var gs = lastGpu;
            if (gs.Ok)
            {
                GpuName.Text = gs.Name;
                VramText.Text = L.F("{0:0.0} / {1:0} ГБ", gs.UsedMiB / 1024, gs.TotalMiB / 1024);
                double pct = gs.UsedMiB * 100 / gs.TotalMiB;
                VramBar.Value = pct;
                Paint(VramBar, Control.ForegroundProperty, pct > 97 ? "Bad" : pct > 90 ? "Warn" : "Accent");
                VramPct.Text = $"{pct:0}%";
                GpuUtil.Text = $"{gs.Util}%";
                GpuPower.Text = $"{gs.PowerW:0} W";
                GpuPower.ToolTip = gs.LimitW > 0 ? L.F("лимит {0:0} W", gs.LimitW) : null;
                GpuTemp.Text = $"{gs.TempC}°";
                Paint(GpuTemp, TextBlock.ForegroundProperty, gs.TempC >= 83 ? "Bad" : gs.TempC >= 75 ? "Warn" : "Text");
                PowerText.Text = gs.LimitW > 0 ? L.F("сейчас {0:0} W", gs.LimitW) : "—";
                GpuName.ToolTip = gpu.Driver != null ? L.T("драйвер ") + gpu.Driver : null;
            }
            else VramText.Text = L.T("нет данных NVML");

            // connection
            int port = srv.Port > 0 && ServerUp ? srv.Port : cfg.Port;
            bool lan = cfg.OnLan;
            UrlLocal.Text = $"http://127.0.0.1:{port}/v1";
            UrlLan.Text = lan ? $"http://{cfg.LanIp}:{port}/v1" : L.T("выключено");
            Paint(UrlLan, TextBlock.ForegroundProperty, lan ? "Text" : "Faint");
            BtnCopyLan.IsEnabled = lan;
            var key = keyShown ? (string.IsNullOrEmpty(cfg.ApiKey) ? L.T("— не задан —") : cfg.ApiKey) : Keys.Mask(cfg.ApiKey);
            KeyText.Text = key;
            KeyText2.Text = key;
            UpdateClients();

            // images
            var aliasOf = srv.Profile ?? Chosen;
            ModelAlias.Text = aliasOf == null ? "—" : aliasOf.Alias ?? IOPath.GetFileNameWithoutExtension(cfg.ModelFile(aliasOf));
            if (!images.Installed)
            {
                ImgState.Text = L.T("ComfyUI не указан"); Paint(ImgDot, Shape.FillProperty, "Faint");
                BtnImg.IsEnabled = false; BtnImgStop.Visibility = Visibility.Collapsed;
            }
            else
            {
                (string text, string dot, string btn) im =
                    imagesBusy ? (L.T("подождите…"), "Warn", L.T("Открыть"))
                    : images.ComfyUp && images.Generating ? (L.T("рисует картинку"), "Good", L.T("Открыть"))
                    // the row is narrow next to the stop button: memory replaces the port when models are loaded
                    : images.ComfyUp ? (!ServerUp && images.HeldMiB >= Images.HeldThresholdMiB ? L.F("работает · {0:0.0} ГБ", images.HeldMiB / 1024.0) : L.F("работает · :{0}", Images.ComfyPort), "Good", L.T("Открыть"))
                    : (L.T("выключен"), "Faint", L.T("Запустить"));
                ImgState.Text = im.text;
                BtnImg.Content = im.btn;
                BtnImg.IsEnabled = !imagesBusy;
                Paint(ImgDot, Shape.FillProperty, im.dot);
                BtnImgStop.Visibility = !imagesBusy && images.ComfyUp ? Visibility.Visible : Visibility.Collapsed;
            }

            // web ui
            if (!webui.Installed)
            {
                WebState.Text = L.T("не установлен"); Paint(WebDot, Shape.FillProperty, "Faint");
                BtnWeb.IsEnabled = false; BtnWebStop.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnWeb.IsEnabled = true;
                (string text, string dot, string btn) w = webStatus switch
                {
                    2 => (L.F("работает · :{0}", cfg.WebUiPort), "Good", L.T("Открыть")),
                    1 => (L.T("запускается…"), "Warn", L.T("Открыть")),
                    _ => (L.T("выключен"), "Faint", L.T("Запустить")),
                };
                WebState.Text = w.text;
                BtnWeb.Content = w.btn;
                Paint(WebDot, Shape.FillProperty, w.dot);
                BtnWebStop.Visibility = webStatus > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ───────────────────────── 24-hour chart ─────────────────────────

        void RenderChart()
        {
            if (!ledgerReady || liveMode) return;
            var now = DateTime.Now;
            var buckets = ledger.Buckets(now, 96, TimeSpan.FromMinutes(15));
            int max = Math.Max(1, buckets.Max(b => b.Req));
            double h = R<double>("ChartHeight"), minBar = R<double>("ChartMinBar");
            ChartBars.Children.Clear();
            foreach (var b in buckets)
            {
                var cell = new Grid { Margin = R<Thickness>("BarGap"), Background = Brushes.Transparent };
                if (b.Req > 0)
                {
                    var bar = new Border { VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = R<CornerRadius>("RBar"), Height = Math.Max(minBar, h * b.Req / max) };
                    bar.SetResourceReference(Border.BackgroundProperty, "Accent");
                    cell.Children.Add(bar);
                }
                else if (b.UpMinutes > 0)
                {
                    var line = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = R<double>("ChartBaseline") };
                    line.SetResourceReference(Border.BackgroundProperty, "Line");
                    cell.Children.Add(line);
                }
                if (b.Failures.Count > 0)
                {
                    var tickMark = new Border { VerticalAlignment = VerticalAlignment.Top, Height = R<double>("ChartTick") };
                    tickMark.SetResourceReference(Border.BackgroundProperty, "Bad");
                    cell.Children.Add(tickMark);
                }
                var tip = new StringBuilder($"{b.Start:HH:mm}–{b.Start.AddMinutes(15):HH:mm}");
                if (b.Req > 0) tip.Append(L.F("\n{0} запрос(ов) · {1} токенов", b.Req, Tokens(b.Gen)));
                else tip.Append(b.UpMinutes > 0 ? L.T("\nсервер работал, запросов не было") : L.T("\nсервер не работал"));
                if (b.Slots > 0 && b.BusyMax > 0) tip.Append(L.F("\nзанято до {0} из {1} слотов", b.BusyMax, b.Slots));
                if (b.Rej > 0) tip.Append(L.F("\nотклонено с неверным ключом: {0}", b.Rej));
                foreach (var f in b.Failures) tip.Append(L.F("\nсбой {0:HH:mm}: {1} {2}", f.At, f.Event, f.Reason).TrimEnd());
                cell.ToolTip = tip.ToString();
                ToolTipService.SetInitialShowDelay(cell, 0);
                ChartBars.Children.Add(cell);
            }

            ChartAxis.Children.Clear();
            ChartAxis.ColumnDefinitions.Clear();
            for (int i = 0; i < 4; i++)
            {
                ChartAxis.ColumnDefinitions.Add(new ColumnDefinition());
                var t = new TextBlock { Text = buckets[i * 24].Start.ToString("HH:mm"), Style = R<Style>("Label") };
                Grid.SetColumn(t, i);
                ChartAxis.Children.Add(t);
            }
            var nowLabel = new TextBlock { Text = L.T("сейчас"), Style = R<Style>("Label"), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(nowLabel, 3);
            ChartAxis.Children.Add(nowLabel);

            int req = buckets.Sum(b => b.Req);
            double upH = buckets.Sum(b => b.UpMinutes) / 60.0;
            ChartSummary.Text = L.F("{0:N0} запросов · {1} токенов · работал {2:0.#} ч", req, Tokens(buckets.Sum(b => b.Gen)), upH);
        }

        // ───────────────────────── live chart ─────────────────────────

        void ChartSpan_Checked(object sender, RoutedEventArgs e)
        {
            liveMode = ChartLive.IsChecked == true;
            ChartBars.Visibility = liveMode ? Visibility.Collapsed : Visibility.Visible;
            LiveChart.Visibility = liveMode ? Visibility.Visible : Visibility.Collapsed;
            ChartTitle.Text = liveMode ? L.T("НАГРУЗКА ЗА 5 МИНУТ") : L.T("НАГРУЗКА ЗА 24 ЧАСА");
            ChartAxis.Tag = null;
            if (liveMode) RenderLive(); else RenderChart();
            if (IsLoaded) try { ui.Set("CHART", liveMode ? "live" : "day"); } catch { }
        }

        void RecordLive()
        {
            var now = DateTime.Now;
            liveSamples.Enqueue((now, lastGpu, activity.Ok ? activity.AggTps : null));
            while (liveSamples.Count > 0 && now - liveSamples.Peek().at > TimeSpan.FromSeconds(LiveBarCount * LiveStep + LiveStep)) liveSamples.Dequeue();
            if (liveMode && PageOverview.Visibility == Visibility.Visible) RenderLive();
        }

        /// <summary>
        /// Last 5 minutes of GPU load as an area chart, one point per 3 seconds on a fixed 0–100 % scale. Reads NVML, so it
        /// shows any work on the card — our server, a benchmark on another port, ComfyUI. A step without a reading breaks
        /// the line. A transparent strip of 100 cells on top carries the tooltips; cells are reused so an open tooltip survives.
        /// </summary>
        void RenderLive()
        {
            if (LiveHits.Children.Count != LiveBarCount)
            {
                LiveHits.Children.Clear();
                for (int i = 0; i < LiveBarCount; i++)
                {
                    var cell = new Border { Background = Brushes.Transparent, Tag = i };
                    cell.MouseEnter += (s, _) => { liveHover = (int)((Border)s).Tag; DrawLive(); };
                    ToolTipService.SetInitialShowDelay(cell, 0);
                    LiveHits.Children.Add(cell);
                }
            }

            // points are aligned to wall-clock 3-second steps so the line slides instead of jittering
            long nowStep = DateTimeOffset.Now.ToUnixTimeSeconds() / LiveStep;
            long firstStep = nowStep - LiveBarCount + 1;
            var groups = liveSamples.Where(x => x.g.Ok)
                .GroupBy(x => new DateTimeOffset(x.at).ToUnixTimeSeconds() / LiveStep)
                .ToDictionary(gr => gr.Key, gr => gr.ToList());
            for (int i = 0; i < LiveBarCount; i++)
            {
                long step = firstStep + i;
                var cell = (Border)LiveHits.Children[i];
                var start = DateTimeOffset.FromUnixTimeSeconds(step * LiveStep).LocalDateTime;
                if (!groups.TryGetValue(step, out var xs))
                {
                    liveUtil[i] = null;
                    cell.ToolTip = L.F("{0:HH:mm:ss}\nнет данных", start);
                    continue;
                }
                double util = xs.Average(x => x.g.Util);
                liveUtil[i] = util;
                var tip = new StringBuilder(L.F("{0:HH:mm:ss}\nзагрузка {1:0}% · {2:0.0} ГБ · {3:0} W", start, util, xs.Max(x => x.g.UsedMiB) / 1024, xs.Average(x => x.g.PowerW)));
                var tps = xs.Where(x => x.tps != null).Select(x => x.tps.Value).DefaultIfEmpty().Max();
                if (tps > 0) tip.Append(L.F("\nсервер генерирует {0:0} т/с", tps));
                cell.ToolTip = tip.ToString();
            }
            DrawLive();

            if (ChartAxis.Tag as string != "live" || DateTime.Now.Second % LiveStep == 0)
            {
                ChartAxis.Tag = "live";
                ChartAxis.Children.Clear();
                ChartAxis.ColumnDefinitions.Clear();
                for (int i = 0; i < 4; i++)
                {
                    ChartAxis.ColumnDefinitions.Add(new ColumnDefinition());
                    var t = new TextBlock { Text = DateTimeOffset.FromUnixTimeSeconds((firstStep + i * LiveBarCount / 4) * LiveStep).LocalDateTime.ToString("HH:mm:ss"), Style = R<Style>("Label") };
                    Grid.SetColumn(t, i);
                    ChartAxis.Children.Add(t);
                }
                var nowLabel = new TextBlock { Text = L.T("сейчас"), Style = R<Style>("Label"), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(nowLabel, 3);
                ChartAxis.Children.Add(nowLabel);
            }

            var g = lastGpu;
            if (!g.Ok) { ChartSummary.Text = L.T("нет данных NVML"); return; }
            var sum = new StringBuilder(L.F("сейчас {0}% · {1:0.0} ГБ · {2:0} W", g.Util, g.UsedMiB / 1024, g.PowerW));
            if (activity.Ok && activity.AggTps is double now && now > 0) sum.Append(L.F(" · {0:0} т/с", now));
            var ok = liveSamples.Where(x => x.g.Ok).ToList();
            if (ok.Count > 0) sum.Append(L.F(" · пик за 5 мин {0}%", ok.Max(x => x.g.Util)));
            ChartSummary.Text = sum.ToString();
        }

        /// <summary>Draws the line, the filled area under it and the hover dot from liveUtil at the chart's current width.</summary>
        void DrawLive()
        {
            double w = LiveChart.ActualWidth, h = R<double>("ChartHeight"), half = R<double>("ChartStroke") / 2, cw = w / LiveBarCount;
            if (w <= 0) return;
            double Y(double u) => Math.Clamp(h - h * u / 100, half, h - half);

            // A poll that overruns skips a step, so holes of up to LiveBridge steps between readings are bridged
            // (and the current step, still being filled, repeats the last reading); longer holes break the line.
            var v = (double?[])liveUtil.Clone();
            for (int i = 0, last = -1; i < LiveBarCount; i++)
            {
                if (liveUtil[i] == null) continue;
                if (last >= 0 && i - last - 1 is > 0 and <= LiveBridge)
                    for (int k = last + 1; k < i; k++) v[k] = liveUtil[last] + (liveUtil[i] - liveUtil[last]) * (k - last) / (i - last);
                last = i;
            }
            if (v[^1] == null && v[^2] != null) v[^1] = v[^2];

            var line = new PathGeometry();
            var area = new PathGeometry();
            for (int i = 0; i < LiveBarCount;)
            {
                if (v[i] == null) { i++; continue; }
                int a = i;
                while (i < LiveBarCount && v[i] != null) i++;
                // a run of values a..i-1 spans from its first cell's left edge to its last cell's right edge
                var pts = new List<Point> { new(a * cw, Y(v[a].Value)) };
                for (int k = a; k < i; k++) pts.Add(new((k + 0.5) * cw, Y(v[k].Value)));
                pts.Add(new(i * cw, Y(v[i - 1].Value)));
                line.Figures.Add(new PathFigure(pts[0], new[] { new PolyLineSegment(pts.Skip(1), true) }, false));
                var poly = new List<Point>(pts) { new(i * cw, h), new(a * cw, h) };
                area.Figures.Add(new PathFigure(poly[0], new[] { new PolyLineSegment(poly.Skip(1), false) }, true));
            }
            line.Freeze();
            area.Freeze();
            LiveLine.Data = line;
            LiveArea.Data = area;

            if (liveHover is int j && liveUtil[j] is double u)
            {
                double d = R<double>("DotSize");
                LiveDot.Margin = new Thickness((j + 0.5) * cw - d / 2, Y(u) - d / 2, 0, 0);
                LiveDot.Visibility = Visibility.Visible;
            }
            else LiveDot.Visibility = Visibility.Collapsed;
        }

        void LiveChart_SizeChanged(object sender, SizeChangedEventArgs e) { if (liveMode) DrawLive(); }

        void LiveChart_MouseLeave(object sender, MouseEventArgs e) { liveHover = null; DrawLive(); }

        // ───────────────────────── statistics tab ─────────────────────────

        /// <summary>"agent|old" → "Агент · запасная модель", "chat|uncensored" → "Чат · без цензуры"; minutes of a server started elsewhere have no mode.</summary>
        string ModeTitle(string mode)
        {
            var parts = mode.Split('|');
            if (parts[0] == "?") return L.T("другой сервер");
            var name = L.T(profiles.FirstOrDefault(p => p.Id == parts[0])?.Name ?? parts[0]);
            return name + Profile.SlotSuffix(parts.Length > 1 ? parts[1] : null);
        }

        /// <summary>One series colour per profile (Series1–4, unknown → Series5); the fallback and uncensored models keep the hue at SeriesAltOpacity.</summary>
        void PaintMode(FrameworkElement el, DependencyProperty prop, string mode)
        {
            var parts = mode.Split('|');
            int i = profiles.FindIndex(p => p.Id == parts[0]);
            el.SetResourceReference(prop, i < 0 ? "Series5" : "Series" + (i % 4 + 1));
            if (parts.Length > 1) el.Opacity = R<double>("SeriesAltOpacity");
        }

        /// <summary>Main model's modes in profile order, then the uncensored model's, the fallback model's, then unknown.</summary>
        List<string> OrderedModes() => ledger.Modes()
            .OrderBy(m => m.Contains("|old") ? 2 : m.Contains("|uncensored") ? 1 : 0)
            .ThenBy(m => { int i = profiles.FindIndex(p => p.Id == m.Split('|')[0]); return i < 0 ? 99 : i; }).ToList();

        static string Hours(int minutes) => minutes >= 60 ? L.F("{0:0.#} ч", minutes / 60.0) : L.F("{0} мин", minutes);

        TextBlock Cell(string text, string style, int row, int col)
        {
            var t = new TextBlock { Text = text, Style = R<Style>(style), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, row); Grid.SetColumn(t, col);
            return t;
        }

        FrameworkElement ModeName(string mode, int row)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Ellipse { Style = R<Style>("Dot"), Margin = R<Thickness>("GapRightS"), VerticalAlignment = VerticalAlignment.Center };
            PaintMode(dot, Shape.FillProperty, mode);
            sp.Children.Add(dot);
            sp.Children.Add(new TextBlock { Text = ModeTitle(mode), Style = R<Style>("Label") });
            Grid.SetRow(sp, row);
            return sp;
        }

        void Table(Grid g, string[] heads)
        {
            g.Children.Clear(); g.RowDefinitions.Clear(); g.ColumnDefinitions.Clear();
            // the name column is two number columns wide: fits "Параллельно · запасная модель"
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            foreach (var _ in heads) g.ColumnDefinitions.Add(new ColumnDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = R<GridLength>("RowHeight") });
            for (int c = 0; c < heads.Length; c++) g.Children.Add(Cell(heads[c], "Caption", 0, c + 1));
        }

        void RenderStats()
        {
            if (!ledgerReady) return;
            var today = DateTime.Today;
            StatsSince.Text = ledger.FirstDay is DateTime first ? L.F("учёт с {0:dd.MM.yyyy}", first) : L.T("учёт только начался");

            // ── totals by period ──
            var rows = new (string name, Totals t)[]
            {
                (L.T("Сегодня"), ledger.Period(today)),
                (L.T("Вчера"), ledger.Period(today.AddDays(-1), today)),
                (L.T("7 дней"), ledger.Period(today.AddDays(-6))),
                (L.T("30 дней"), ledger.Period(today.AddDays(-29))),
                (L.T("Всё время"), ledger.Period(null)),
            };
            Table(StatsTable, new[] { L.T("ЗАПРОСОВ"), L.T("ТОКЕНОВ"), L.T("РАБОТАЛ"), L.T("СБОЕВ"), L.T("ОТКЛОНЕНО") });
            for (int r = 0; r < rows.Length; r++)
            {
                var (name, t) = rows[r];
                int row = r + 1;
                StatsTable.RowDefinitions.Add(new RowDefinition { Height = R<GridLength>("RowHeight") });
                StatsTable.Children.Add(Cell(name, "Label", row, 0));
                StatsTable.Children.Add(Cell(t.Req.ToString("N0"), "BodyStrong", row, 1));
                StatsTable.Children.Add(Cell(Tokens(t.Gen), "BodyStrong", row, 2));
                StatsTable.Children.Add(Cell(Hours(t.UpMinutes), "BodyStrong", row, 3));
                var fail = Cell(t.Failures.ToString(), "BodyStrong", row, 4);
                if (t.Failures > 0) fail.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                StatsTable.Children.Add(fail);
                var rej = Cell(t.Rej.ToString("N0"), "BodyStrong", row, 5);
                if (t.Rej > 0) rej.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
                StatsTable.Children.Add(rej);
            }

            // ── 30 days, each bar stacked by mode ──
            var modes = OrderedModes();
            var days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).ToList();
            var cube = days.Select(d => modes.Select(m => ledger.Period(d, d.AddDays(1), m)).ToList()).ToList();
            var dayTotals = days.Select(d => ledger.Period(d, d.AddDays(1))).ToList();
            bool gen = DaysGen.IsChecked == true, up = DaysUp.IsChecked == true;
            Func<Totals, double> metric = gen ? t => t.Gen : up ? t => t.UpMinutes : t => t.Req;
            string Fmt(double v) => gen ? Tokens((long)v) + L.T(" токенов") : up ? Hours((int)v) : L.F("{0:N0} запросов", v);
            double max = Math.Max(1, cube.Max(ms => ms.Sum(metric)));
            double h = R<double>("ChartHeight"), minBar = R<double>("ChartMinBar");
            DayBars.Children.Clear();
            for (int i = 0; i < days.Count; i++)
            {
                var cell = new Grid { Margin = R<Thickness>("BarGap"), Background = Brushes.Transparent };
                double total = cube[i].Sum(metric);
                if (total > 0)
                {
                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
                    var parts = modes.Select((m, k) => (m, v: metric(cube[i][k]))).Where(x => x.v > 0).Reverse().ToList();
                    double full = Math.Max(minBar, h * total / max);
                    for (int k = 0; k < parts.Count; k++)
                    {
                        // the top segment carries the bar's rounded corners
                        var seg = new Border { Height = full * parts[k].v / total, CornerRadius = k == 0 ? R<CornerRadius>("RBar") : default };
                        PaintMode(seg, Border.BackgroundProperty, parts[k].m);
                        stack.Children.Add(seg);
                    }
                    cell.Children.Add(stack);
                }
                else if (dayTotals[i].UpMinutes > 0)
                {
                    var line = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = R<double>("ChartBaseline") };
                    line.SetResourceReference(Border.BackgroundProperty, "Line");
                    cell.Children.Add(line);
                }
                if (dayTotals[i].Failures > 0)
                {
                    var mark = new Border { VerticalAlignment = VerticalAlignment.Top, Height = R<double>("ChartTick") };
                    mark.SetResourceReference(Border.BackgroundProperty, "Bad");
                    cell.Children.Add(mark);
                }
                var dt = dayTotals[i];
                var tip = new StringBuilder($"{days[i]:ddd dd.MM}: {Fmt(total)}");
                for (int k = 0; k < modes.Count; k++)
                    if (metric(cube[i][k]) > 0) tip.Append($"\n   {ModeTitle(modes[k])}: {Fmt(metric(cube[i][k]))}");
                if (dt.UpMinutes > 0) tip.Append(L.F("\nработал {0} · {1:N0} запросов · {2} токенов", Hours(dt.UpMinutes), dt.Req, Tokens(dt.Gen)));
                else tip.Append(L.T("\nсервер не работал"));
                if (dt.Failures > 0) tip.Append(L.F("\nсбоев: {0}", dt.Failures));
                if (dt.Rej > 0) tip.Append(L.F("\nотклонено с неверным ключом: {0}", dt.Rej));
                cell.ToolTip = tip.ToString();
                ToolTipService.SetInitialShowDelay(cell, 0);
                DayBars.Children.Add(cell);
            }
            DayAxis.Children.Clear(); DayAxis.ColumnDefinitions.Clear();
            for (int i = 0; i < 30; i++) DayAxis.ColumnDefinitions.Add(new ColumnDefinition());
            foreach (int i in new[] { 0, 7, 14, 21 })
            {
                var t = new TextBlock { Text = days[i].ToString("dd.MM"), Style = R<Style>("Label") };
                Grid.SetColumn(t, i); Grid.SetColumnSpan(t, 7);
                DayAxis.Children.Add(t);
            }
            var todayLabel = new TextBlock { Text = L.T("сегодня"), Style = R<Style>("Label"), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(todayLabel, 26); Grid.SetColumnSpan(todayLabel, 4);
            DayAxis.Children.Add(todayLabel);
            var month = ledger.Period(days[0]);
            DaysSummary.Text = L.F("{0:N0} запросов · {1} токенов · работал {2}", month.Req, Tokens(month.Gen), Hours(month.UpMinutes));

            DaysLegend.Children.Clear();
            foreach (var m in modes)
            {
                var item = ModeName(m, 0);
                item.Margin = R<Thickness>("GapRight");
                DaysLegend.Children.Add(item);
            }

            // ── all time by mode ──
            Table(ModesTable, new[] { L.T("ЗАПРОСОВ"), L.T("ТОКЕНОВ"), L.T("РАБОТАЛ"), L.T("ДОЛЯ ЗАПРОСОВ") });
            long allReq = Math.Max(1, ledger.Period(null).Req);
            for (int r = 0; r < modes.Count; r++)
            {
                var t = ledger.Period(null, null, modes[r]);
                int row = r + 1;
                ModesTable.RowDefinitions.Add(new RowDefinition { Height = R<GridLength>("RowHeight") });
                ModesTable.Children.Add(ModeName(modes[r], row));
                ModesTable.Children.Add(Cell(t.Req.ToString("N0"), "BodyStrong", row, 1));
                ModesTable.Children.Add(Cell(Tokens(t.Gen), "BodyStrong", row, 2));
                ModesTable.Children.Add(Cell(Hours(t.UpMinutes), "BodyStrong", row, 3));
                ModesTable.Children.Add(Cell($"{100.0 * t.Req / allReq:0}%", "BodyStrong", row, 4));
            }
        }

        void DaysMetric_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) RenderStats(); }

        static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1e6:0.0} M" : n >= 1000 ? $"{n / 1e3:0.#} K" : n.ToString();

        // ───────────────────────── log ─────────────────────────

        void DrainLog()
        {
            if (incoming.IsEmpty) return;
            logScroll ??= FindChild<ScrollViewer>(LogList);
            bool atEnd = logScroll == null || logScroll.VerticalOffset >= logScroll.ScrollableHeight - 4;

            var own = new StringBuilder();
            int n = 0;
            while (n < 3000 && incoming.TryDequeue(out var l))
            {
                log.Add(l);
                if (l.Kind != LogKind.Server && !l.FromServer)
                {
                    own.Append(DateTime.Now.ToString("yyyy-MM-dd ")).Append(l.Time).Append("  ").AppendLine(l.Text);
                    events.Insert(0, l);
                    if (events.Count > 40) events.RemoveAt(events.Count - 1);
                }
                n++;
            }
            if (own.Length > 0) { try { File.AppendAllText(IOPath.Combine(Paths.Logs, "studio.log"), own.ToString()); } catch { } }
            if (log.Count > 6000) log.RemoveFirst(log.Count - 5000);
            if (atEnd && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            // P2: a busy log drains every 120 ms; the poll refreshes every second anyway
            if (n > 0 && clock.ElapsedMilliseconds - lastUiMs >= 500) UpdateUi();
        }

        void Filter_Changed(object sender, RoutedEventArgs e)
        {
            logView.Filter = OnlyImportant.IsChecked == true ? o => ((LogLine)o).Important : null;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        }

        void LogCopy_Click(object sender, RoutedEventArgs e) => CopyLog(LogList.SelectedItems.Count > 1 ? LogList.SelectedItems.Cast<LogLine>() : logView.Cast<LogLine>());

        void LogList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { CopyLog(LogList.SelectedItems.Cast<LogLine>()); e.Handled = true; }
        }

        void CopyLog(IEnumerable<LogLine> lines)
        {
            var text = string.Join(Environment.NewLine, lines.Select(l => l.Time + "  " + l.Text));
            if (text.Length > 0) Copy(text);
        }

        void LogClear_Click(object sender, RoutedEventArgs e) => log.Clear();

        void LogOpen_Click(object sender, RoutedEventArgs e)
        {
            var f = srv.LogFile;
            if (string.IsNullOrEmpty(f) || !File.Exists(f))
                f = Directory.GetFiles(Paths.Logs, "server-*.log").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (f != null) Proc.Open(f); else Proc.Open(Paths.Logs);
        }

        void ShowLog_Click(object sender, RoutedEventArgs e) => TabLog.IsChecked = true;

        // ───────────────────────── tools ─────────────────────────

        double webStartAt;

        async void Web_Click(object sender, RoutedEventArgs e)
        {
            if (webStatus > 0) { Proc.Open(webui.Url); return; }
            try
            {
                await webui.Start();
                webStatus = 1;
                openWebWhenReady = true;
                webStartAt = clock.Elapsed.TotalSeconds;
                srv.Emit(L.F("Запускаю Open WebUI на :{0} — браузер откроется, когда он будет готов (обычно 20–60 с).", cfg.WebUiPort));
                if (!ServerUp) srv.Emit(L.T("Сервер модели не запущен — в Open WebUI не будет модели, пока вы его не запустите."), LogKind.Warn);
            }
            catch (Exception ex) { Error(L.T("Open WebUI не запустился: ") + ex.Message); }
            UpdateUi();
        }

        async void WebStop_Click(object sender, RoutedEventArgs e)
        {
            openWebWhenReady = false;
            await webui.Stop();
            webStatus = 0;
            srv.Emit(L.T("Open WebUI остановлен."));
            UpdateUi();
        }

        /// <summary>ComfyUI for pictures. Image models need most of the card while they draw.</summary>
        async void Img_Click(object sender, RoutedEventArgs e)
        {
            if (imagesBusy) return;
            if (images.ComfyUp) { Proc.Open(images.Url); return; }
            if (ServerUp)
            {
                var used = lastGpu.Ok ? L.F("{0:0.0} ГБ", lastGpu.UsedMiB / 1024) : L.T("почти всю память");
                var r = MessageBox.Show(this,
                    L.F("Моделям картинок во время генерации нужна большая часть видеопамяти, а языковая модель сейчас занимает {0}. Вместе они могут не поместиться.\n\n", used) +
                    L.T("Да — остановить сервер и запустить ComfyUI.\nНет — запустить ComfyUI, сервер оставить."),
                    "Qwen Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes)
                {
                    if (busy) return;
                    busyN++;
                    try
                    {
                        if (!await ConfirmInterrupt(L.T("Остановить сервер"))) return;
                        await Stop("images");
                    }
                    finally { busyN--; UpdateUi(); }
                }
            }
            imagesBusy = true; UpdateUi();
            try
            {
                srv.Emit(L.F("Запускаю ComfyUI (:{0}) — обычно до минуты…", Images.ComfyPort));
                var (ok, message) = await images.Start();
                if (ok) { srv.Emit(L.F("ComfyUI готов: {0}", images.Url), LogKind.Good); Proc.Open(images.Url); }
                else srv.Emit(message, LogKind.Error);
            }
            finally { imagesBusy = false; UpdateUi(); }
        }

        async void ImgStop_Click(object sender, RoutedEventArgs e)
        {
            if (imagesBusy) return;
            await images.Probe();
            if (images.Generating && !Ask(L.T("ComfyUI сейчас рисует картинку — генерация оборвётся.\n\nОстановить ComfyUI?"))) return;
            imagesBusy = true; UpdateUi();
            try
            {
                await images.Stop();
                srv.Emit(images.ComfyUp ? L.T("ComfyUI не остановился: порт занят не им?") : L.T("ComfyUI остановлен."), images.ComfyUp ? LogKind.Warn : LogKind.Info);
            }
            finally { imagesBusy = false; UpdateUi(); }
        }

        void Opencode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // the desktop app if it is installed, otherwise the terminal agent
                var psi = File.Exists(Paths.OpencodeDesktop)
                    ? new ProcessStartInfo(Paths.OpencodeDesktop) { UseShellExecute = true, WorkingDirectory = Paths.Base }
                    : new ProcessStartInfo("cmd.exe", "/k opencode") { UseShellExecute = true, WorkingDirectory = Paths.Base };
                Process.Start(psi);
                if (!ServerUp) srv.Emit(L.T("OpenCode открыт, но сервер модели не запущен."), LogKind.Warn);
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            var text = ((Button)sender).Tag switch
            {
                "local" => UrlLocal.Text,
                "lan" => UrlLan.Text,
                _ => ModelAlias.Text,
            };
            Copy(text);
        }

        /// <summary>Win32 copy, not WPF Clipboard: WPF's flush step froze the window for seconds under Parsec (see ClipboardText).</summary>
        void Copy(string text, bool secret = false)
        {
            if (!ClipboardText.Set(text, secret)) srv.Emit(L.T("Буфер обмена занят другой программой — не скопировано, попробуйте ещё раз."), LogKind.Warn);
        }

        void KeyEye_Click(object sender, RoutedEventArgs e) { keyShown = !keyShown; UpdateUi(); }

        void KeyCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(cfg.ApiKey)) Copy(cfg.ApiKey, secret: true);
        }

        async void KeyNew_Click(object sender, RoutedEventArgs e)
        {
            if (!Ask(L.T("Создать новый API-ключ?\n\nСтарый ключ перестанет работать: другим клиентам нужно будет выдать новый. ") +
                     L.T("opencode обновится автоматически, если ключ прописан в его конфиге. Сервер будет перезапущен."))) return;
            if (busy) return;
            busyN++; UpdateUi();
            try { await RotateKey(); }
            finally { busyN--; UpdateUi(); }
        }

        async Task RotateKey()
        {
            if (ServerUp && !await ConfirmInterrupt(L.T("Сменить ключ и перезапустить сервер"))) return;
            var (newKey, oc) = Keys.Rotate(cfg);
            keyShown = false;
            Copy(newKey, secret: true);
            srv.Emit(L.T("Новый API-ключ сохранён в server_config.env") + (oc ? L.T(" и в конфиг opencode") : "") + L.T(" и скопирован в буфер обмена."), LogKind.Good);
            if (ServerUp && srv.Profile != null)
            {
                var p = srv.Profile;
                await Stop("new key");
                await StartProfile(p, checks: false);
            }
            if (webStatus > 0)
            {
                // Open WebUI reads the key from its own database: write it there and restart it
                await webui.Stop();
                await webui.Start();
                webStatus = 1;
                srv.Emit(L.T("Open WebUI перезапущен с новым ключом."), LogKind.Good);
            }
            UpdateUi();
        }

        // ───────────────────────── settings ─────────────────────────

        static readonly (string key, string title, string filter)[] PathKeys =
        {
            ("SERVER_EXE", "Сервер llama.cpp", "llama-server.exe|llama-server.exe"),
            ("MODEL_PATH", "Модель", "GGUF|*.gguf"),
            ("FALLBACK_SERVER_EXE", "Запасной сервер", "llama-server.exe|llama-server.exe"),
            ("FALLBACK_MODEL_PATH", "Запасная модель", "GGUF|*.gguf"),
            ("UNCENSORED_MODEL_PATH", "Модель без цензуры", "GGUF|*.gguf"),
            ("MMPROJ_PATH", "Модуль зрения (mmproj)", "GGUF|*.gguf"),
            ("COMFYUI_DIR", "ComfyUI (картинки)", "ComfyUI main.py или run_nvidia_gpu.bat|main.py;run_nvidia_gpu.bat"),
        };

        void FillSettings()
        {
            PathsPanel.Children.Clear();
            foreach (var (key, title, filter) in PathKeys)
            {
                var row = new Grid { Margin = R<Thickness>("GapBelowS") };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = R<GridLength>("FieldColumn") });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = L.T(title), Style = R<Style>("Label"), VerticalAlignment = VerticalAlignment.Center };
                var box = new TextBox { Text = cfg.Env.Get(key), Height = R<double>("HControl"), FontFamily = R<FontFamily>("Mono"), FontSize = 12 };
                var ok = new TextBlock { Style = R<Style>("Icon"), Margin = R<Thickness>("GapLeftS") };
                var browse = new Button { Content = L.T("Обзор…"), Margin = R<Thickness>("GapLeftS") };
                void Mark()
                {
                    bool exists = File.Exists(Paths.Resolve(box.Text)) || key == "COMFYUI_DIR" && images.Installed;
                    ok.Text = exists ? "" : "";
                    Paint(ok, TextBlock.ForegroundProperty, exists ? "Good" : "Bad");
                    ok.ToolTip = exists ? Paths.Resolve(box.Text) : L.T("файл не найден");
                }
                void Commit()
                {
                    var v = box.Text.Trim();
                    if (v != cfg.Env.Get(key)) { cfg.Env.Set(key, v); srv.Emit($"{key} = {v}"); if (key.EndsWith("MODEL_PATH")) { ShowModels(); ShowOptions(); UpdateUi(); } }
                    Mark();
                }
                box.LostFocus += (_, _) => Commit();
                box.KeyDown += (_, a) => { if (a.Key == Key.Enter) Commit(); };
                browse.Click += (_, _) =>
                {
                    var cur = Paths.Resolve(box.Text);
                    var dlg = new OpenFileDialog { Filter = filter + L.T("|Все файлы|*.*"), InitialDirectory = Directory.Exists(IOPath.GetDirectoryName(cur)) ? IOPath.GetDirectoryName(cur) : Paths.Base };
                    if (dlg.ShowDialog(this) != true) return;
                    var rel = IOPath.GetRelativePath(Paths.Base, dlg.FileName);
                    box.Text = rel.StartsWith("..") ? dlg.FileName : rel;
                    Commit();
                };
                Mark();
                Grid.SetColumn(box, 1); Grid.SetColumn(ok, 2); Grid.SetColumn(browse, 3);
                row.Children.Add(label); row.Children.Add(box); row.Children.Add(ok); row.Children.Add(browse);
                PathsPanel.Children.Add(row);
            }

            LanToggle.Checked -= Lan_Changed; LanToggle.Unchecked -= Lan_Changed;
            LanToggle.IsChecked = cfg.OnLan;
            LanToggle.Checked += Lan_Changed; LanToggle.Unchecked += Lan_Changed;
            PortBox.Text = cfg.Port.ToString();
            var ips = Net.LanIps();
            LanIpText.Text = ips.Count == 0 ? L.T("сеть не найдена") : string.Join("   ", ips);

            FillAutostart();
            FillArchive();
            int watts = cfg.Env.GetInt("POWER_LIMIT_W", 0);
            BtnPower.Visibility = watts > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnPower.Content = L.F("Установить {0} W", watts);
            AboutText.Text = L.F("Qwen Studio {0} · папка проекта: {1}", typeof(App).Assembly.GetName().Version?.ToString(3), Paths.Base);
        }

        void FillAutostart()
        {
            AutostartToggle.Checked -= Autostart_Changed; AutostartToggle.Unchecked -= Autostart_Changed;
            AutoServerToggle.Checked -= AutoServer_Changed; AutoServerToggle.Unchecked -= AutoServer_Changed;
            bool on = false;
            try { on = Autostart.Enabled; } catch { }
            AutostartToggle.IsChecked = on;
            AutoServerToggle.IsChecked = ui.Get("AUTOSTART_SERVER") == "1";
            AutoServerToggle.IsEnabled = on;
            AutostartToggle.Checked += Autostart_Changed; AutostartToggle.Unchecked += Autostart_Changed;
            AutoServerToggle.Checked += AutoServer_Changed; AutoServerToggle.Unchecked += AutoServer_Changed;

            var last = Profile.FromKey(profiles, ui.Get("LAST_START"));
            var info = last != null ? L.F("Последний запущенный режим: {0}.", last.Title) : L.T("Сервер ещё не запускался из этой версии — режим для автозапуска появится после первого запуска.");
            bool stale = false;
            try { stale = Autostart.Stale; } catch { }
            if (stale) info += L.T(" Автозапуск записан для другой копии Qwen Studio — выключите и включите, чтобы открывалась эта.");
            AutostartInfo.Text = info;
        }

        void Autostart_Changed(object sender, RoutedEventArgs e)
        {
            bool on = AutostartToggle.IsChecked == true;
            try
            {
                Autostart.Set(on);
                srv.Emit(on ? L.T("Qwen Studio будет открываться при входе в Windows.") : L.T("Автозапуск Qwen Studio выключен."));
            }
            catch (Exception ex) { Error(L.T("Не удалось изменить автозапуск: ") + ex.Message); }
            FillAutostart();
        }

        void AutoServer_Changed(object sender, RoutedEventArgs e)
        {
            bool on = AutoServerToggle.IsChecked == true;
            try { ui.Set("AUTOSTART_SERVER", on ? "1" : "0"); } catch { }
            srv.Emit(on ? L.T("При автозапуске сервер поднимется в последнем режиме (если видеокарта свободна).") : L.T("При автозапуске сервер запускаться не будет."));
            FillAutostart();
        }

        void FillArchive()
        {
            var (files, bytes) = LogArchive.Pending(srv.LogFile);
            ArchiveText.Text = files > 0
                ? L.F("{0} журнал(ов) старше {1} дней · {2:0.#} МБ", files, LogArchive.KeepDays, bytes / 1048576.0)
                : L.F("журналы старше {0} дней упаковываются в архив раз в сутки", LogArchive.KeepDays);
            BtnArchive.IsEnabled = files > 0;
        }

        async void Archive_Click(object sender, RoutedEventArgs e) => await ArchiveLogs(manual: true);

        async Task ArchiveLogs(bool manual)
        {
            lastArchive = DateTime.Now;
            var current = srv.LogFile;
            LogArchive.Result r;
            try { r = await Task.Run(() => LogArchive.Run(current)); }
            catch (Exception ex) { srv.Emit(L.T("Архив журналов: ") + ex.Message, LogKind.Warn); return; }
            if (r.Files > 0) srv.Emit(L.F("Старые журналы упакованы: {0} шт., {1:0.#} МБ → logs\\studio\\archive, оригиналы в корзине.", r.Files, r.Bytes / 1048576.0), LogKind.Good);
            else if (manual && r.Errors.Count == 0) srv.Emit(L.T("Старых журналов нет."));
            foreach (var err in r.Errors) srv.Emit(L.T("Архив журналов: ") + err, LogKind.Warn);
            FillArchive();
        }

        async void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PageOverview == null) return;
            bool settings = TabSettings.IsChecked == true, logPage = TabLog.IsChecked == true, stats = TabStats.IsChecked == true;
            PageOverview.Visibility = !settings && !logPage && !stats ? Visibility.Visible : Visibility.Collapsed;
            PageLog.Visibility = logPage ? Visibility.Visible : Visibility.Collapsed;
            PageStats.Visibility = stats ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
            Sidebar.Visibility = settings || stats ? Visibility.Collapsed : Visibility.Visible;
            if (stats) RenderStats();
            if (logPage && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            if (settings)
            {
                FillAutostart();
                FillArchive();
                int rej = ledgerReady ? ledger.RejectedSince(DateTime.Now.AddHours(-24)) : 0;
                RejectedInfo.Text = L.F("За сутки отклонено {0} запрос(ов) с неверным ключом — у кого-то из клиентов старый ключ.", rej);
                RejectedInfo.Visibility = rej > 0 ? Visibility.Visible : Visibility.Collapsed;
                FwText.Text = L.T("проверяю…");
                Paint(FwDot, Shape.FillProperty, "Faint");
                bool ok = await Firewall.AllPresent(cfg);
                FwText.Text = ok ? L.T("порт сервера открыт для локальной сети") : L.T("правила не найдены");
                Paint(FwDot, Shape.FillProperty, ok ? "Good" : "Warn");
                BtnFw.Content = ok ? L.T("Пересоздать правила") : L.T("Открыть порты");
            }
        }

        void Lan_Changed(object sender, RoutedEventArgs e)
        {
            cfg.Env.Set("HOST", LanToggle.IsChecked == true ? "0.0.0.0" : "127.0.0.1");
            srv.Emit(LanToggle.IsChecked == true ? L.T("Доступ из сети включён — применится при следующем запуске сервера.") : L.T("Сервер будет доступен только с этого компьютера — после перезапуска."));
            UpdateUi();
        }

        void Port_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(PortBox.Text.Trim(), out var p) && p is > 0 and < 65536)
            {
                if (p != cfg.Port) { cfg.Env.Set("PORT", p.ToString()); srv.Emit(L.F("Порт сервера: {0} (при следующем запуске)", p)); }
            }
            else PortBox.Text = cfg.Port.ToString();
            UpdateUi();
        }

        async void Firewall_Click(object sender, RoutedEventArgs e)
        {
            if (!Ask(L.F("Разрешить входящие подключения на порт сервера {0} из локальной подсети?\n\nOpen WebUI остаётся доступен только с этого компьютера. Понадобятся права администратора.", cfg.Port))) return;
            bool ok = await Firewall.Add(cfg);
            srv.Emit(ok ? L.T("Правила брандмауэра созданы.") : L.T("Правила брандмауэра не созданы (отказ или ошибка UAC)."), ok ? LogKind.Good : LogKind.Warn);
            Tab_Checked(null, null);
        }

        async void Power_Click(object sender, RoutedEventArgs e)
        {
            int watts = cfg.Env.GetInt("POWER_LIMIT_W", 0);
            if (watts <= 0) return;
            if (!Ask(L.F("Установить лимит мощности видеокарты {0} W и задачу автозапуска NVIDIA-PowerLimit-{1}W?\n\nПонадобятся права администратора.", watts, watts))) return;
            bool ok = await Power.Apply(watts);
            srv.Emit(ok ? L.F("Лимит мощности {0} W установлен.", watts) : L.T("Лимит мощности не установлен (отказ или ошибка UAC)."), ok ? LogKind.Good : LogKind.Warn);
        }

        Release latest;

        async void LlamaUpd_Click(object sender, RoutedEventArgs e)
        {
            if (latest != null) { await InstallLlama(latest); return; }
            BtnLlamaUpd.IsEnabled = false;
            LlamaVer.Text = L.T("проверяю…");
            int? local; Release remote;
            try
            {
                var localTask = Updates.LocalLlamaBuild(Paths.Resolve(cfg.Env.Get("SERVER_EXE", Paths.NewestLlama())));
                remote = await Updates.LatestLlama();
                local = await localTask;
            }
            catch (Exception ex) { LlamaVer.Text = L.T("Не удалось проверить: ") + ex.Message; return; }
            finally { BtnLlamaUpd.IsEnabled = true; }
            if (remote == null) { LlamaVer.Text = L.F("установлен b{0} · GitHub недоступен", local); return; }
            if (local == null || remote.Build > local)
            {
                latest = remote;
                LlamaVer.Text = L.F("установлен {0} · доступен b{1}", (local == null ? "?" : "b" + local), remote.Build);
                BtnLlamaUpd.Content = L.F("Скачать b{0} ({1} МБ)", remote.Build, remote.Size >> 20);
            }
            else LlamaVer.Text = L.F("b{0} — последняя версия", local);
        }

        async Task InstallLlama(Release r)
        {
            if (!Ask(L.F("Скачать llama.cpp b{0} (CUDA 12, {1} МБ) с github.com/ggml-org/llama.cpp в папку llama-b{2}?\n\nТекущая сборка останется на месте.", r.Build, r.Size >> 20, r.Build))) return;
            BtnLlamaUpd.IsEnabled = false;
            UpdProgress.Visibility = Visibility.Visible;
            try
            {
                var exe = await Updates.InstallLlama(r, new Progress<string>(s => UpdProgress.Text = s));
                if (exe == null) { Error(L.T("В архиве не нашёлся llama-server.exe.")); return; }
                UpdProgress.Text = L.F("Готово: {0}", exe);
                srv.Emit(L.F("llama.cpp b{0} установлен в {1}", r.Build, exe), LogKind.Good);
                if (Ask(L.F("Сделать b{0} основным сервером?\n\nСейчас: {1}\nБудет: {2}\n\nВернуть можно в «Сервер и модели». Применится при следующем запуске.", r.Build, cfg.Env.Get("SERVER_EXE"), exe)))
                {
                    srv.Emit($"SERVER_EXE: {cfg.Env.Get("SERVER_EXE")} → {exe}");
                    cfg.Env.Set("SERVER_EXE", exe);
                    FillSettings();
                }
                latest = null;
                BtnLlamaUpd.Content = L.T("Проверить");
                LlamaVer.Text = L.F("b{0} установлен", r.Build);
            }
            catch (Exception ex) { Error(L.T("Не удалось установить: ") + ex.Message); }
            finally { BtnLlamaUpd.IsEnabled = true; }
        }

        string webLatest;

        async void WebUpd_Click(object sender, RoutedEventArgs e)
        {
            if (webLatest != null)
            {
                if (!Ask(L.F("Обновить Open WebUI до {0} через uv? Это займёт пару минут.", webLatest) + (webStatus > 0 ? L.T("\n\nOpen WebUI сейчас работает — он будет остановлен.") : ""))) return;
                if (webStatus > 0) { await webui.Stop(); webStatus = 0; }
                BtnWebUpd.IsEnabled = false;
                srv.Emit(L.T("Обновляю Open WebUI…"));
                var (code, _) = await Updates.UpgradeWebUi(l => incoming.Enqueue(new LogLine { Time = DateTime.Now.ToString("HH:mm:ss"), Text = "uv: " + l, Kind = LogKind.Server, Important = false }));
                srv.Emit(code == 0 ? L.T("Open WebUI обновлён.") : L.F("uv завершился с кодом {0}.", code), code == 0 ? LogKind.Good : LogKind.Error);
                webLatest = null;
                BtnWebUpd.Content = L.T("Проверить");
                BtnWebUpd.IsEnabled = true;
            }
            BtnWebUpd.IsEnabled = false;
            WebVer.Text = L.T("проверяю…");
            var localTask = Updates.LocalWebUi();
            var remote = await Updates.LatestWebUi();
            var local = await localTask;
            BtnWebUpd.IsEnabled = true;
            if (local == null) { WebVer.Text = L.T("не установлен через uv"); return; }
            if (remote == null) { WebVer.Text = L.F("установлен {0} · PyPI недоступен", local); return; }
            if (remote != local)
            {
                webLatest = remote;
                WebVer.Text = L.F("установлен {0} · доступен {1}", local, remote);
                BtnWebUpd.Content = L.F("Обновить до {0}", remote);
            }
            else WebVer.Text = L.F("{0} — последняя версия", local);
        }

        void ProfilesEdit_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { Paths.Profiles }, UseShellExecute = false }); } catch { }
        }

        void ProfilesReload_Click(object sender, RoutedEventArgs e)
        {
            var keep = selected?.Id;
            LoadProfiles();
            Select(profiles.FirstOrDefault(p => p.Id == keep));
            srv.Emit(L.F("Профили перечитаны: {0}.", profiles.Count));
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e) => Proc.Open(Paths.Base);
        void OpenLogs_Click(object sender, RoutedEventArgs e) => Proc.Open(Paths.Logs);

        // ───────────────────────── window ─────────────────────────

        async void OnClosing(object sender, CancelEventArgs e)
        {
            if (closingForReal || !ServerUp) return;
            var r = MessageBox.Show(this,
                L.T("Сервер модели продолжит работать в фоне — клиенты не отключатся, а при следующем запуске Qwen Studio подхватит его.\n\nОстановить сервер перед выходом?"),
                "Qwen Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                if (!await ConfirmInterrupt(L.T("Остановить сервер и выйти"))) return;
                await Stop("exit");
                closingForReal = true;
                Close();
            }
        }

        void ThemeToggle_Click(object sender, RoutedEventArgs e) => SetTheme(Theme.IsDark ? ThemeMode.Light : ThemeMode.Dark);

        void Theme_Checked(object sender, RoutedEventArgs e)
        {
            var m = Theme.Parse((string)((RadioButton)sender).Tag);
            if (m != Theme.Mode) SetTheme(m);
        }

        void SetTheme(ThemeMode m)
        {
            try { ui.Set("THEME", m.ToString()); } catch { }
            Theme.Apply(m);
        }

        void OnThemeChanged()
        {
            BtnTheme.Content = Theme.IsDark ? "" : "";     // sun / moon
            BtnTheme.ToolTip = Theme.IsDark ? L.T("Светлая тема") : L.T("Тёмная тема");
            (Theme.Mode switch { ThemeMode.Light => ThemeLight, ThemeMode.Dark => ThemeDark, _ => ThemeSystem }).IsChecked = true;
            if (new WindowInteropHelper(this).Handle != IntPtr.Zero) DarkTitleBar();
        }

        /// <summary>Interface language, live: XAML texts via L.Apply, everything code writes by re-running what writes it. The log keeps its language.</summary>
        void Lang_Checked(object sender, RoutedEventArgs e)
        {
            var lang = (string)((FrameworkElement)sender).Tag;
            if (!IsLoaded || lang == L.Current) return;
            L.Set(lang);
            try { ui.Set("LANG", lang); } catch { }
            L.Apply(this);
            if (File.Exists(Paths.OpencodeDesktop)) TxtOpencodeKind.Text = L.T("приложение для ПК");
            ChartTitle.Text = liveMode ? L.T("НАГРУЗКА ЗА 5 МИНУТ") : L.T("НАГРУЗКА ЗА 24 ЧАСА");
            ChartAxis.Tag = null;
            if (liveMode) RenderLive(); else RenderChart();
            LoadProfiles();
            ShowModels();
            ShowOptions();
            FillSettings();
            OnThemeChanged();
            Tab_Checked(null, null);
            UpdateUi();
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void DarkTitleBar()
        {
            var h = new WindowInteropHelper(this).Handle;
            int on = Theme.IsDark ? 1 : 0, caption = Theme.ColorRef("Bg"), text = Theme.ColorRef("Text");
            DwmSetWindowAttribute(h, 20, ref on, 4);
            DwmSetWindowAttribute(h, 35, ref caption, 4);
            DwmSetWindowAttribute(h, 36, ref text, 4);
        }

        // ───────────────────────── helpers ─────────────────────────

        /// <summary>A design token from App.xaml (see DESIGN.md).</summary>
        T R<T>(string key) => (T)FindResource(key);

        /// <summary>Binds a brush property to a palette key, so it follows theme switches.</summary>
        static T Paint<T>(T el, DependencyProperty dp, string key) where T : FrameworkElement { el.SetResourceReference(dp, key); return el; }

        bool Ask(string text) => MessageBox.Show(this, text, "Qwen Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        void Error(string text) => MessageBox.Show(this, text, "Qwen Studio", MessageBoxButton.OK, MessageBoxImage.Warning);

        static string Elapsed(TimeSpan t) =>
            t.TotalHours >= 1 ? L.F("{0} ч {1} мин", (int)t.TotalHours, t.Minutes) : t.TotalMinutes >= 1 ? L.F("{0} мин {1} с", (int)t.TotalMinutes, t.Seconds) : L.F("{0} с", t.Seconds);

        static string Ago(TimeSpan t) =>
            t.TotalSeconds < 60 ? L.F("{0} с", Math.Max(1, (int)t.TotalSeconds)) : t.TotalMinutes < 60 ? L.F("{0} мин", (int)t.TotalMinutes) : L.F("{0} ч {1} мин", (int)t.TotalHours, t.Minutes);

        static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) return t;
                var deeper = FindChild<T>(c);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
