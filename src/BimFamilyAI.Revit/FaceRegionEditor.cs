using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BimFamilyAI.Core;
using Microsoft.Win32;

namespace BimFamilyAI.Revit;

internal sealed class FaceRegionEditor : Window
{
    private readonly ObjRegionPreview preview;
    private readonly ObjRegionEdits edits;
    private readonly string source, extraction;
    private readonly ComboBox components = new();
    private readonly ListBox regions = new() { Height = 95, DisplayMemberPath = nameof(RegionItem.Caption) };
    private readonly TextBox regionName = new(), rgb = new() { Text = "0,80,220" }, angle = new() { Text = "25" };
    private readonly TextBox fallback = new();
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock selectionInfo = new();
    private readonly Viewport3D viewport = new();
    private readonly OrthographicCamera camera = new() { Width = 2.0 };
    private readonly Model3DGroup models = new();
    private readonly Grid layout = new();
    private readonly HashSet<string> hidden = [];
    private readonly Stack<HashSet<int>> undo = new();
    private HashSet<int> selection = [];
    private double yaw, pitch;
    private Point? drag, dragStart;
    private bool leftDrag;
    private bool syncing, working, closed, draft;
    internal Task PendingFileOperation { get; private set; } = Task.CompletedTask;
    internal ObjRegionPlan? AcceptedPlan { get; private set; }
    private sealed record RegionItem(ObjFaceRegion Region)
    {
        public string Caption => Region.Name ?? Region.RegionId;
    }
    private string Active => (string)components.SelectedItem;
    private string? Editing => (regions.SelectedItem as RegionItem)?.Region.RegionId;

    internal FaceRegionEditor(string source, ObjExtractionResult extracted, ObjRegionPreview preview, ObjRegionPlan? initial)
    {
        this.source = source;
        extraction = extracted.OutputDirectory;
        this.preview = preview;
        edits = new(extracted.Manifest.SourceSha256);
        if (initial is not null) edits.Load(initial);
        Title = "BIM Family AI — Regiones de caras";
        Width = Math.Min(1090, SystemParameters.WorkArea.Width * .94);
        Height = Math.Min(790, SystemParameters.WorkArea.Height * .93);
        MinWidth = 690;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = layout;
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(330) });
        var surface = new Border { Background = new SolidColorBrush(Color.FromRgb(29, 40, 55)), Child = viewport };
        layout.Children.Add(surface);
        viewport.Camera = camera;
        viewport.Children.Add(new ModelVisual3D { Content = models });
        viewport.Children.Add(new ModelVisual3D { Content = new AmbientLight(Color.FromRgb(85, 85, 85)) });
        viewport.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Color.FromRgb(175, 175, 175), new(-1, 2, -3)) });
        surface.MouseLeftButtonDown += BeginDrag;
        surface.MouseRightButtonDown += BeginDrag;
        void BeginDrag(object sender, MouseButtonEventArgs e)
        {
            if (drag is not null) return;
            leftDrag = e.ChangedButton == MouseButton.Left;
            drag = dragStart = e.GetPosition(surface);
            surface.CaptureMouse();
            e.Handled = true;
        }
        surface.MouseMove += (_, e) =>
        {
            if (drag is not { } previous) return;
            var point = e.GetPosition(surface);
            if (dragStart is { } start && (point - start).Length < 4) return;
            dragStart = null;
            yaw -= (point.X - previous.X) * .012;
            pitch = Math.Clamp(pitch + (point.Y - previous.Y) * .012, -1.54, 1.54);
            drag = point;
            UpdateCamera();
        };
        surface.MouseLeftButtonUp += EndDrag;
        surface.MouseRightButtonUp += EndDrag;
        void EndDrag(object sender, MouseButtonEventArgs e)
        {
            if (drag is null || leftDrag != (e.ChangedButton == MouseButton.Left)) return;
            bool pick = leftDrag && dragStart is not null;
            surface.ReleaseMouseCapture();
            if (pick) SelectFace(e.GetPosition(viewport));
        }
        surface.LostMouseCapture += (_, _) => drag = dragStart = null;
        surface.MouseWheel += (_, e) =>
        {
            camera.Width = Math.Clamp(camera.Width * Math.Pow(.80, e.Delta / 120.0), .015, 12);
            e.Handled = true;
        };
        surface.SizeChanged += (_, _) => Fit();

        var panel = new StackPanel { Margin = new Thickness(13) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        layout.Children.Add(scroll);
        Label("Clic selecciona · Ctrl + clic agrega o retira · arrastre gira · rueda acerca");
        Add("Reencuadrar", ResetCamera);
        Field("Componente activa", components);
        Add("Mostrar u ocultar componente activa", () => { if (!hidden.Add(Active)) hidden.Remove(Active); Refresh(); });
        Add("Mostrar todas las componentes", () => { hidden.Clear(); Refresh(); });
        panel.Children.Add(selectionInfo);
        Field("Ángulo máximo de crecimiento (°)", angle);
        Row(("Crecer", Grow), ("Deshacer selección", Undo), ("Limpiar", () => ChangeSelection([])));
        Field("Regiones de la componente", regions);
        Row(("Nueva región", NewRegion), ("Eliminar región", DeleteRegion));
        Field("Nombre de región", regionName);
        Field("RGB de región (0–255)", rgb);
        Add("Aplicar nombre, RGB y caras a región", PutRegion);
        Field("RGB de respaldo para caras sin material (opcional)", fallback);
        Add("Aplicar respaldo", () =>
        {
            edits.SetFallback(Active, string.IsNullOrWhiteSpace(fallback.Text) ? null : ParseColor(fallback.Text));
            Refresh();
        });
        Row(("Guardar plan…", () => PendingFileOperation = Persist(false)),
            ("Cargar plan…", () => PendingFileOperation = Persist(true)));
        Row(("Usar regiones", () => PendingFileOperation = Accept()), ("Cancelar", Close));
        panel.Children.Add(feedback);
        components.ItemsSource = extracted.Manifest.Components.Select(p => p.Id).ToArray();
        components.SelectionChanged += (_, _) => { if (!syncing) ReloadRegions(null); };
        regions.SelectionChanged += (_, _) => { if (!syncing) LoadDraft(); };
        regionName.TextChanged += (_, _) => { if (!syncing) draft = true; };
        rgb.TextChanged += (_, _) => { if (!syncing) draft = true; };
        fallback.TextChanged += (_, _) => { if (!syncing) draft = true; };
        components.SelectedIndex = 0;
        Closed += (_, _) => closed = true;
        ResetCamera();

        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 2) });
        void Field(string label, Control field) { Label(label); panel.Children.Add(field); }
        Button CreateButton(string label, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 3, 4, 2) };
            button.Click += (_, _) =>
            {
                feedback.Text = "";
                try { action(); }
                catch (ArgumentException ex) { feedback.Text = ex.Message; }
            };
            return button;
        }
        void Add(string label, Action action) => panel.Children.Add(CreateButton(label, action));
        void Row(params (string Label, Action Action)[] actions)
        {
            var row = new WrapPanel();
            foreach (var (label, action) in actions) row.Children.Add(CreateButton(label, action));
            panel.Children.Add(row);
        }
    }

    private void ResetCamera() { yaw = -1; pitch = .57; Fit(); UpdateCamera(); }
    private void Fit() => camera.Width = 1.85 * Math.Max(1, viewport.ActualWidth / Math.Max(1, viewport.ActualHeight));
    private void UpdateCamera()
    {
        var target = new Point3D(0, 0, .5);
        var offset = new Vector3D(3 * Math.Cos(pitch) * Math.Cos(yaw), 3 * Math.Cos(pitch) * Math.Sin(yaw), 3 * Math.Sin(pitch));
        camera.Position = target + offset;
        camera.LookDirection = -offset;
        camera.UpDirection = new(0, 0, 1);
    }
    private void SelectFace(Point point)
    {
        if (working || viewport.ActualWidth <= 0) return;
        var forward = camera.LookDirection;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double unitsPerPixel = camera.Width / viewport.ActualWidth;
        var origin = camera.Position + right * ((point.X - viewport.ActualWidth / 2) * unitsPerPixel) +
            up * ((viewport.ActualHeight / 2 - point.Y) * unitsPerPixel);
        var hit = preview.Pick(new((float)origin.X, (float)origin.Y, (float)origin.Z),
            new((float)forward.X, (float)forward.Y, (float)forward.Z), hidden);
        if (hit is null) return;
        if (hit.ComponentId != Active)
        {
            feedback.Text = $"La cara visible pertenece a {hit.ComponentId}. Active u oculte esa componente.";
            return;
        }
        var next = new HashSet<int>(selection);
        ObjRegionEdits.Select(next, hit.OriginalFace, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        ChangeSelection(next);
        feedback.Text = $"Cara original {hit.OriginalFace}";
    }
    private void ChangeSelection(HashSet<int> next)
    {
        if (selection.SetEquals(next)) return;
        undo.Push(selection);
        selection = next;
        draft = true;
        Refresh();
    }
    private void Undo()
    {
        if (undo.TryPop(out var previous)) { selection = previous; Refresh(); }
    }
    private void Grow()
    {
        if (selection.Count == 0) throw new ArgumentException("Seleccione una cara para iniciar el crecimiento.");
        if (!double.TryParse(angle.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double degrees) &&
            !double.TryParse(angle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees))
            throw new ArgumentException("Indique un ángulo de 0 a 180 grados.");
        var blocked = edits.Get(Active).Regions.Where(r => r.RegionId != Editing)
            .SelectMany(r => r.OriginalFaceNumbers).ToHashSet();
        ChangeSelection(preview.Grow(selection, degrees, blocked));
    }
    private void ReloadRegions(string? id)
    {
        syncing = true;
        var component = edits.Get(Active);
        var items = component.Regions.Select(region => new RegionItem(region)).ToArray();
        regions.ItemsSource = items;
        regions.SelectedItem = items.FirstOrDefault(item => item.Region.RegionId == id);
        fallback.Text = Format(component.FallbackColor);
        syncing = false;
        LoadDraft();
    }
    private void LoadDraft()
    {
        syncing = true;
        var region = (regions.SelectedItem as RegionItem)?.Region;
        regionName.Text = region?.Name ?? region?.RegionId ?? "";
        rgb.Text = Format(region?.Color ?? new(0, 80, 220));
        selection = region?.OriginalFaceNumbers.ToHashSet() ?? [];
        undo.Clear();
        draft = false;
        syncing = false;
        Refresh();
    }
    private void NewRegion()
    {
        syncing = true;
        regions.SelectedIndex = -1;
        regionName.Clear();
        syncing = false;
        draft = selection.Count > 0;
        Refresh();
    }
    private void PutRegion()
    {
        var color = ParseColor(rgb.Text);
        var back = string.IsNullOrWhiteSpace(fallback.Text) ? null : ParseColor(fallback.Text);
        edits.Put(Active, Editing, regionName.Text, color, selection);
        edits.SetFallback(Active, back);
        var saved = edits.Get(Active).Regions.Last();
        ReloadRegions(saved.RegionId);
        feedback.Text = $"Región aplicada · {saved.OriginalFaceNumbers.Count} caras.";
    }
    private void DeleteRegion()
    {
        if (Editing is not { } id) throw new ArgumentException("Seleccione una región para eliminar.");
        edits.Delete(Active, id);
        ReloadRegions(null);
    }
    private static ObjRegionColor ParseColor(string value)
    {
        var parts = value.Split(',');
        if (parts.Length == 3 && byte.TryParse(parts[0], out byte r) && byte.TryParse(parts[1], out byte g) && byte.TryParse(parts[2], out byte b))
            return new(r, g, b);
        throw new ArgumentException("Escriba tres valores RGB entre 0 y 255 separados por comas.");
    }
    private static string Format(ObjRegionColor? color) => color is null ? "" : $"{color.Red},{color.Green},{color.Blue}";

    private void Refresh()
    {
        selectionInfo.Text = $"{selection.Count} caras seleccionadas · {hidden.Count} componentes ocultas";
        var snapshot = edits.Snapshot();
        var colors = snapshot.Components.SelectMany(c => c.Regions)
            .SelectMany(r => r.OriginalFaceNumbers.Select(f => (f, r.Color))).ToDictionary(x => x.f, x => x.Color);
        var styles = snapshot.Components.ToDictionary(c => c.ComponentId);
        models.Children.Clear();
        foreach (var group in preview.Triangles.Where(t => !hidden.Contains(t.ComponentId)).GroupBy(t =>
            t.ComponentId == Active && selection.Contains(t.OriginalFace) && draft ? new ObjRegionColor(255, 190, 50) :
            colors.GetValueOrDefault(t.OriginalFace) ??
            (t.HasOriginalMaterial ? t.OriginalColor : styles.GetValueOrDefault(t.ComponentId)?.FallbackColor) ??
            (t.ComponentId == Active ? new(160, 177, 194) : new(110, 125, 143))))
        {
            var mesh = new MeshGeometry3D();
            foreach (var triangle in group)
            {
                var normal = Vector3.Normalize(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A));
                int first = mesh.Positions.Count;
                mesh.Positions.Add(new(triangle.A.X, triangle.A.Y, triangle.A.Z));
                mesh.Positions.Add(new(triangle.B.X, triangle.B.Y, triangle.B.Z));
                mesh.Positions.Add(new(triangle.C.X, triangle.C.Y, triangle.C.Z));
                for (int i = 0; i < 3; i++)
                {
                    mesh.Normals.Add(new(normal.X, normal.Y, normal.Z));
                    mesh.TriangleIndices.Add(first + i);
                }
            }
            mesh.Freeze();
            var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(group.Key.Red, group.Key.Green, group.Key.Blue)));
            material.Freeze();
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
            model.Freeze();
            models.Children.Add(model);
        }
    }

    private async Task Persist(bool load)
    {
        string? file;
        if (load)
        {
            var picker = new OpenFileDialog { Filter = "Plan de regiones (*.json)|*.json", CheckFileExists = true };
            file = picker.ShowDialog(this) == true ? picker.FileName : null;
        }
        else
        {
            var picker = new SaveFileDialog { Filter = "Plan de regiones (*.json)|*.json", DefaultExt = ".json", AddExtension = true, OverwritePrompt = false };
            file = picker.ShowDialog(this) == true ? picker.FileName : null;
        }
        if (file is null) return;
        await ValidateAsync(async snapshot =>
        {
            if (load)
            {
                var plan = await Task.Run(() => ObjComponentExtractor.ReadRegionPlan(file, source, extraction));
                if (closed) return;
                edits.Load(plan);
                ReloadRegions(null);
                feedback.Text = "Plan recuperado y verificado contra el OBJ.";
            }
            else
            {
                await Task.Run(() => ObjComponentExtractor.SaveNewRegionPlan(file,
                    ObjComponentExtractor.ValidateRegionPlan(source, extraction, snapshot)));
                feedback.Text = "Plan guardado.";
            }
        });
    }
    private async Task Accept()
    {
        await ValidateAsync(async snapshot =>
        {
            if (snapshot.Components.Count > 0)
                snapshot = await Task.Run(() => ObjComponentExtractor.ValidateRegionPlan(source, extraction, snapshot));
            if (closed) return;
            AcceptedPlan = snapshot;
            DialogResult = true;
        });
    }
    private async Task ValidateAsync(Func<ObjRegionPlan, Task> action)
    {
        working = true;
        layout.IsEnabled = false;
        try { await action(edits.Snapshot()); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or JsonException)
        { if (!closed) feedback.Text = ex.Message; }
        finally { working = false; if (!closed) layout.IsEnabled = true; }
    }
}
