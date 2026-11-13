using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QwenStudio.Core;
using IOPath = System.IO.Path;

namespace QwenStudio
{
    /// <summary>Settings → Модели: the list of models.json and an editor of one model on a working copy, saved by «Сохранить».</summary>
    public partial class MainWindow
    {
        ModelDef editing;      // working copy in the editor
        string editingId;      // the model it came from; null for a new one

        void FillModels()
        {
            ModelsList.Children.Clear();
            foreach (var m in models)
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = m.Name, Style = R<Style>("BodyStrong"), TextTrimming = TextTrimming.CharacterEllipsis });
                var modes = m.Profiles == null ? L.T("общие режимы") : L.F("свои режимы: {0}", m.Profiles.Count);
                text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(m.Label) ? modes : modes + " · " + L.T(m.Label), Style = R<Style>("Label"), TextTrimming = TextTrimming.CharacterEllipsis });
                var rb = new RadioButton { Style = R<Style>("ProfileCard"), GroupName = "editmodel", Content = text, IsChecked = m.Id == editingId, ToolTip = cfg.PathOf(m.Model) };
                rb.Click += (_, _) => ShowModelEditor(m);
                ModelsList.Children.Add(rb);
            }
            if (editing == null || editingId != null && models.All(x => x.Id != editingId)) ShowModelEditor(Current);
        }

        void ModelAdd_Click(object sender, RoutedEventArgs e)
        {
            ShowModelEditor(null);
            foreach (RadioButton rb in ModelsList.Children) rb.IsChecked = false;
        }

        /// <summary>Opens a model in the editor; null starts a new one.</summary>
        void ShowModelEditor(ModelDef m)
        {
            editingId = m?.Id;
            editing = m?.Clone() ?? new ModelDef { Id = "m" + DateTime.Now.ToString("yyMMddHHmmss"), Name = "", Model = "" };
            foreach (RadioButton rb in ModelsList.Children) rb.IsChecked = ModelsList.Children.IndexOf(rb) == models.IndexOf(m);
            var ed = ModelEditor;
            ed.Children.Clear();

            var info = new TextBlock { Style = R<Style>("Label"), Margin = R<Thickness>("GapBelowS"), TextWrapping = TextWrapping.Wrap };
            void Info()
            {
                var f = cfg.PathOf(editing.Model);
                if (string.IsNullOrWhiteSpace(editing.Model)) { info.Text = L.T("Выберите файл GGUF."); return; }
                if (!File.Exists(f)) { info.Text = L.F("Файл не найден: {0}", f); return; }
                var thinks = Gguf.Thinks(f);
                info.Text = L.F("{0:0.0} ГБ", new FileInfo(f).Length / 1e9) + (thinks == true ? L.T(" · умеет размышлять") : thinks == false ? L.T(" · без размышлений") : "");
            }

            ed.Children.Add(Field(L.T("Название"), editing.Name, v => editing.Name = v, mono: false));
            ed.Children.Add(Field(L.T("Подпись"), editing.Label, v => editing.Label = v, mono: false,
                                  tip: L.T("Короткая метка в списке моделей: «запасная», «без цензуры», «тест». Можно оставить пустой.")));
            TextBox name = (TextBox)((Grid)ed.Children[0]).Children[1];
            ed.Children.Add(Field(L.T("Файл модели"), editing.Model, v => { editing.Model = v; Info(); }, filter: "GGUF|*.gguf",
                                  picked: f => { if (name.Text.Trim() == "") name.Text = ModelDef.NameOf(f); }));
            Info();
            ed.Children.Add(info);
            ed.Children.Add(Field(L.T("Сервер llama.cpp"), editing.Server, v => editing.Server = v, filter: "llama-server.exe|llama-server.exe"));
            ed.Children.Add(Field(L.T("Модуль зрения"), editing.Mmproj, v => editing.Mmproj = v, filter: "GGUF|*.gguf"));
            ed.Children.Add(new TextBlock { Style = R<Style>("Hint"), Text = L.T("Пустые сервер и модуль зрения — общие, из «Сервер и пути».") });

            var own = new CheckBox { Content = L.T("Свои режимы для этой модели"), IsChecked = editing.Profiles != null, Margin = R<Thickness>("GapBelowS") };
            own.ToolTip = L.T("Без галочки модель запускается в общих режимах из profiles.json. С галочкой — в своих: они начинаются как копия общих.");
            var modes = new StackPanel();
            own.Checked += (_, _) =>
            {
                editing.Profiles = new ModelDef { Profiles = shared }.Clone().Profiles;
                foreach (var p in editing.Profiles) { p.Name = L.T(p.Name); p.Badge = null; }   // the badge follows ctx and -np from now on
                ShowModes(modes);
            };
            own.Unchecked += (_, _) => { editing.Profiles = null; ShowModes(modes); };
            ed.Children.Add(own);
            ed.Children.Add(modes);
            ShowModes(modes);

            var foot = new DockPanel { Margin = R<Thickness>("GapTop"), LastChildFill = false };
            var del = new Button { Content = L.T("Удалить модель"), Style = R<Style>("DangerOutline"), Height = R<double>("HControl"), IsEnabled = editingId != null };
            del.Click += (_, _) => DeleteModel();
            var save = new Button { Content = L.T("Сохранить"), Style = R<Style>("Primary"), Height = R<double>("HControl") };
            save.Click += (_, _) => SaveModel();
            DockPanel.SetDock(save, Dock.Right);
            foot.Children.Add(del);
            foot.Children.Add(save);
            ed.Children.Add(foot);
        }

        /// <summary>The model's own modes as editable blocks, or a line naming the shared ones.</summary>
        void ShowModes(StackPanel panel)
        {
            panel.Children.Clear();
            if (editing.Profiles == null)
            {
                panel.Children.Add(new TextBlock { Style = R<Style>("Hint"), Text = L.F("Общие режимы: {0}. Правятся в profiles.json.", string.Join(", ", shared.Select(p => L.T(p.Name)))) });
                return;
            }
            foreach (var p in editing.Profiles.ToList())
            {
                var block = new StackPanel();
                var head = new DockPanel { Margin = R<Thickness>("GapBelowS") };
                var remove = new Button { Style = R<Style>("IconBtn"), Content = "", ToolTip = L.T("Удалить режим"), IsEnabled = editing.Profiles.Count > 1 };
                remove.Click += (_, _) => { editing.Profiles.Remove(p); ShowModes(panel); };
                DockPanel.SetDock(remove, Dock.Right);
                var ctx = new TextBox { Text = p.Ctx.ToString(), Width = R<double>("NumberField"), Height = R<double>("HControl"), Margin = R<Thickness>("GapLeftS"), VerticalContentAlignment = VerticalAlignment.Center };
                ctx.TextChanged += (_, _) => p.Ctx = int.TryParse(ctx.Text.Trim(), out var n) ? n : 0;
                DockPanel.SetDock(ctx, Dock.Right);
                var ctxLabel = new TextBlock { Text = L.T("контекст"), Style = R<Style>("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = R<Thickness>("GapLeftS") };
                DockPanel.SetDock(ctxLabel, Dock.Right);
                var nm = new TextBox { Text = p.Name, Height = R<double>("HControl"), FontWeight = FontWeights.SemiBold, VerticalContentAlignment = VerticalAlignment.Center };
                nm.TextChanged += (_, _) => p.Name = nm.Text.Trim();
                head.Children.Add(remove); head.Children.Add(ctx); head.Children.Add(ctxLabel); head.Children.Add(nm);
                block.Children.Add(head);
                var args = new TextBox { Text = Profile.ArgsText(p.Args), FontFamily = R<FontFamily>("Mono"), FontSize = 12, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
                args.TextChanged += (_, _) => p.Args = Profile.ParseArgs(args.Text);
                block.Children.Add(new TextBlock { Text = L.T("Аргументы запуска"), Style = R<Style>("Label"), Margin = R<Thickness>("GapBelowS") });
                block.Children.Add(args);
                panel.Children.Add(new Border { Style = R<Style>("Card"), Margin = R<Thickness>("GapBelowS"), Child = block });
            }
            var add = new Button { Content = L.T("+ Режим"), Style = R<Style>("Link"), HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (_, _) =>
            {
                var last = editing.Profiles.Last();
                int n = 1; while (editing.Profiles.Any(x => x.Id == "mode-" + n)) n++;
                var copy = new ModelDef { Profiles = new List<Profile> { last } }.Clone().Profiles[0];
                copy.Id = "mode-" + n; copy.Name = L.F("Режим {0}", n); copy.Badge = null; copy.Description = null;
                editing.Profiles.Add(copy);
                ShowModes(panel);
            };
            panel.Children.Add(add);
            panel.Children.Add(new TextBlock { Style = R<Style>("Hint"), Text = L.T("Studio сама добавляет -m, -c, --host, --port, --mmproj и API-ключ; здесь — всё остальное. --reasoning меняет тумблер «Размышления».") });
        }

        /// <summary>A settings row: label, text box, and for a path a «Обзор…» button.</summary>
        Grid Field(string label, string value, Action<string> set, bool mono = true, string filter = null, string tip = null, Action<string> picked = null)
        {
            var row = new Grid { Margin = R<Thickness>("GapBelowS") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = R<GridLength>("FieldColumn") });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Text = value ?? "", Height = R<double>("HControl"), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = tip };
            if (mono) { box.FontFamily = R<FontFamily>("Mono"); box.FontSize = 12; }
            box.TextChanged += (_, _) => set(box.Text.Trim());
            row.Children.Add(new TextBlock { Text = label, Style = R<Style>("Label"), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip });
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            if (filter != null)
            {
                var browse = new Button { Content = L.T("Обзор…"), Margin = R<Thickness>("GapLeftS") };
                browse.Click += (_, _) => { if (Browse(box.Text, filter) is string f) { box.Text = f; picked?.Invoke(f); } };
                Grid.SetColumn(browse, 2);
                row.Children.Add(browse);
            }
            return row;
        }

        /// <summary>A file dialog starting at the current value; the pick relative to the project folder when it lies inside.</summary>
        string Browse(string current, string filter)
        {
            var cur = cfg.PathOf(current);
            var dir = IOPath.GetDirectoryName(cur);
            var dlg = new OpenFileDialog { Filter = filter + L.T("|Все файлы|*.*"), InitialDirectory = Directory.Exists(dir) ? dir : Paths.Base };
            if (dlg.ShowDialog(this) != true) return null;
            var rel = IOPath.GetRelativePath(Paths.Base, dlg.FileName);
            return rel.StartsWith("..") || IOPath.IsPathRooted(rel) ? dlg.FileName : rel;
        }

        void SaveModel()
        {
            var e = editing;
            string Bad(string path) => !string.IsNullOrWhiteSpace(path) && !File.Exists(cfg.PathOf(path)) ? cfg.PathOf(path) : null;
            string problem =
                string.IsNullOrWhiteSpace(e.Name) ? L.T("Укажите название модели.")
                : string.IsNullOrWhiteSpace(e.Model) || !File.Exists(cfg.PathOf(e.Model)) ? L.F("Не найден файл модели:\n{0}", cfg.PathOf(e.Model))
                : Bad(e.Server) is string s ? L.F("Не найден llama-server:\n{0}", s)
                : Bad(e.Mmproj) is string v ? L.F("Не найден модуль зрения:\n{0}", v)
                : e.Profiles?.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Name)) != null ? L.T("У каждого режима должно быть название.")
                : e.Profiles?.FirstOrDefault(p => p.Ctx < 1024) is Profile c ? L.F("Контекст режима «{0}» — число не меньше 1024.", c.Name)
                : null;
            if (problem != null) { Error(problem); return; }
            var list = models.Select(x => x.Id == editingId ? e : x).ToList();
            if (editingId == null) list.Add(e);
            try { ModelDef.SaveAll(list); }
            catch (Exception ex) { Error(L.T("models.json не записан: ") + ex.Message); return; }
            srv.Emit(L.F("Модель «{0}» сохранена.", e.Name), LogKind.Good);
            AfterModelsChanged(e.Id);
        }

        void DeleteModel()
        {
            if (editingId == null) return;
            if (models.Count == 1) { Error(L.T("Это единственная модель в списке: её можно изменить, но не удалить.")); return; }
            if (ServerUp && srv.Profile?.Slot == editingId) { Error(L.T("Сервер сейчас работает на этой модели. Остановите его или переключите на другую модель.")); return; }
            var name = models.First(x => x.Id == editingId).Name;
            if (!Ask(L.F("Убрать «{0}» из списка моделей? Файл модели останется на диске.", name))) return;
            try { ModelDef.SaveAll(models.Where(x => x.Id != editingId)); }
            catch (Exception ex) { Error(L.T("models.json не записан: ") + ex.Message); return; }
            srv.Emit(L.F("Модель «{0}» убрана из списка.", name));
            AfterModelsChanged(null);
        }

        void AfterModelsChanged(string show)
        {
            var keep = selected?.Id;
            LoadProfiles();
            Select(profiles.FirstOrDefault(p => p.Id == keep));
            ShowModels();
            editing = null;
            editingId = show;
            FillModels();
            if (show != null) ShowModelEditor(models.FirstOrDefault(x => x.Id == show));
            UpdateUi();
        }
    }
}
