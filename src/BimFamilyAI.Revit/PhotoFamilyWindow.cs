using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BimFamilyAI.Core;
using BimFamilyAI.Meshy;
using Microsoft.Win32;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;

namespace BimFamilyAI.Revit;

internal sealed class PhotoFamilyWindow : Window
{
    private readonly PhotoFamilyExternalEvent bridge;
    private readonly ComboBox mode=new() {ItemsSource=new[] {"Prueba (simulado, 0 créditos)","Real (Meshy API, puede consumir créditos)"},SelectedIndex=0};
    private readonly ComboBox scenario=new() {ItemsSource=new[] {"Normal","Seguimiento lento","HTTP 401","HTTP 402","HTTP 429","Timeout de creación","Tarea fallida","URL vencida","OBJ inválido","Creación incierta"},SelectedIndex=0};
    private readonly ComboBox axis=new() {ItemsSource=new[] {"Z","Y","X"},SelectedIndex=0};
    private readonly ComboBox familyMode=new() {ItemsSource=new[] {"OBJ completo · una importación", "Componentes separados · importaciones independientes", "Regiones de caras · materiales por selección"},SelectedIndex=0};
    private readonly TextBox polycount=new() {Text="4000",Width=90,HorizontalAlignment=HorizontalAlignment.Left};
    private readonly TextBox name=new() {Text="SIM_Familia_Foto"}, description=new() {AcceptsReturn=true,Height=43}, height=new();
    private readonly TextBox taskId=new(), objPath=new() {IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=42}, rfaPath=new() {IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=42};
    private readonly TextBlock modeNotice=new(), photoName=new() {Text="Seleccione una fotografía local.",TextWrapping=TextWrapping.Wrap}, previewNotice=new() {TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock status=new() {Text="Modo de prueba: sin clave, sin red Meshy y sin costo.",TextWrapping=TextWrapping.Wrap}, traffic=new() {TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock phase=new() {TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold};
    private readonly Image preview=new() {Height=135,Stretch=Stretch.Uniform};
    private readonly ProgressBar progressBar=new() {Minimum=0,Maximum=100,Height=12};
    private readonly ListBox history=new() {Height=110};
    private readonly CheckBox editMaterials=new() {Content="Materiales y apariencia: elegir antes de guardar (sin generar IA)",Margin=new Thickness(0,8,0,4)};
    private readonly TextBlock componentInfo=new() {Text="La modalidad por componentes usa mallas desconectadas por índices, sin nombres semánticos.",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock labelIdentity=new() {Text="Seleccione una importación en el editor de familias de Revit y pulse Consultar selección.",TextWrapping=TextWrapping.Wrap};
    private readonly TextBox labelName=new();
    private readonly ListBox existingLabels=new() {Height=118};
    private readonly List<Control> controls=[];
    private Button stop=null!, generate=null!, query=null!, resume=null!, createRfa=null!, openRfa=null!, reconcile=null!, retry=null!, objFolder=null!, rfaFolder=null!, analyzeComponents=null!, configureColors=null!;
    private Button consultLabel=null!, listLabels=null!, saveLabel=null!, clearLabel=null!;
    private PhotoGenerationSession session=null!;
    private JsonTaskStore store=null!;
    private MeshySimulation? simulation;
    private GeneratedObjResult? result;
    private string? photoPath, lastRfa, localObjPath;
    private ObjStructureResult? componentReport;
    private IReadOnlyList<ComponentColor> componentColors=[];
    private ObjRegionPlan? regionPlan;
    private FaceRegionEditor? regionEditor;
    private Button editRegions=null!;
    private ComponentLabelItem? selectedLabel;
    private CancellationTokenSource? operationCancellation;
    private Task? running;
    private bool busy, closed, shutdown;
    private Window? confirmation;
    private int operationVersion;
    private bool IsSimulated=>mode.SelectedIndex==0;
    private string? AvailableObjPath=>localObjPath ?? result?.LocalPath;
    private sealed record HistoryItem(MeshyTaskRecord Record)
    {
        public override string ToString()=> $"{(Record.State?.ToString() ?? Record.Submission.ToString())} · {(Record.TaskId is null ? "reserva sin ID" : Record.TaskId[..Math.Min(8,Record.TaskId.Length)])} · {(Record.AttemptedAt ?? Record.CreatedAt)?.ToLocalTime().ToString("g") ?? "sin fecha"} · {(Record.Download==DownloadState.Validated ? "OBJ disponible" : "sin OBJ local")}";
    }

    internal PhotoFamilyWindow(PhotoFamilyExternalEvent bridge,IntPtr owner)
    {
        this.bridge=bridge;
        Title="BIM Family AI — Generador de familias con IA";
        Width=Math.Min(1060,SystemParameters.WorkArea.Width*0.93);
        Height=Math.Min(790,SystemParameters.WorkArea.Height*0.88);
        MinWidth=700;MinHeight=400;FontSize=13;
        Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(245,247,250));
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        new System.Windows.Interop.WindowInteropHelper(this).Owner=owner;
        var root=new DockPanel {Margin=new Thickness(16)};Content=root;
        var top=new StackPanel {Margin=new Thickness(0,0,0,10)};DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        top.Children.Add(new TextBlock {Text="BIM Family AI",FontSize=23,FontWeight=FontWeights.SemiBold,Foreground=Accent(),Margin=new Thickness(0,0,0,3)});
        Label(top,"Fotografía  →  modelo OBJ  →  familia RFA");
        top.Children.Add(phase);
        var bottom=new Border {Background=Brushes.White,BorderBrush=BorderColor(),BorderThickness=new Thickness(1),Padding=new Thickness(10),Margin=new Thickness(0,8,0,0)};
        DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        var feedback=new StackPanel();bottom.Child=feedback;
        feedback.Children.Add(progressBar);feedback.Children.Add(status);feedback.Children.Add(traffic);
        var scroll=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};root.Children.Add(scroll);
        var content=new StackPanel {Margin=new Thickness(0,0,6,0)};scroll.Content=content;

        var photograph=Section(content,"01   Fotografía y configuración");
        var photoGrid=new Grid();photoGrid.ColumnDefinitions.Add(new() {Width=new GridLength(185)});photoGrid.ColumnDefinitions.Add(new());photograph.Children.Add(photoGrid);
        photoGrid.Children.Add(new Border {Background=Brushes.WhiteSmoke,Child=preview,MinHeight=140,Margin=new Thickness(0,0,12,0)});
        var photoDetails=new StackPanel();Grid.SetColumn(photoDetails,1);photoGrid.Children.Add(photoDetails);
        photoDetails.Children.Add(photoName);photoDetails.Children.Add(previewNotice);
        AddButton(photoDetails,"Seleccionar fotografía JPG / PNG / WebP",()=>Run(SelectPhotoAsync),true,false);
        Field(photograph,"Modo de trabajo",mode);photograph.Children.Add(modeNotice);
        Label(photograph,"Meshy · Image to 3D · Meshy T2 / smart-topology · sin texturas · OBJ");
        Field(photograph,"Polígonos objetivo (100–15000)",polycount);
        Label(photograph,"Generación real nueva: costo estimado de 5 créditos API por tarea (tarifa verificada 16/11/2026; puede cambiar). La simulación usa un OBJ fijo y cuesta 0.");
        Field(photograph,"Escenario de prueba (solo modo simulado)",scenario);

        var generation=Section(content,"02   Generación y seguimiento");
        generate=AddButton(generation,"Generar modelo 3D · nueva tarea",()=>Run(GenerateAsync),true,true);
        Label(generation,"Solo una generación NUEVA en modo real puede consumir créditos. Se pedirá autorización explícita antes de enviarla.");
        stop=AddButton(generation,"Detener seguimiento local",()=>operationCancellation?.Cancel(),false);
        Label(generation,"Detener o cerrar no cancela la tarea remota. Conserve su ID para reanudar sin crear otra generación.");
        Label(generation,"Historial local y recuperación · sin nueva generación pagada");
        generation.Children.Add(history);controls.Add(history);
        AddButton(generation,"Actualizar historial",()=>Run(RefreshHistoryAsync));
        Field(generation,"ID completo de tarea (seleccione un registro o péguelo)",taskId);
        var row=new WrapPanel();generation.Children.Add(row);
        query=AddButton(row,"Consultar estado",()=>Run(QueryAsync));
        resume=AddButton(row,"Continuar / recuperar OBJ",()=>Run(ResumeAsync));
        AddButton(row,"Copiar ID",()=> {try {Clipboard.SetText(RequireId());}catch(Exception) {SetStatus("No se pudo copiar un ID válido al portapapeles.");}});
        var advanced=new Expander {Header="Opciones avanzadas · conciliación y reintento",Margin=new Thickness(0,8,0,0)};generation.Children.Add(advanced);
        var advancedPanel=new StackPanel();advanced.Content=advancedPanel;
        reconcile=AddButton(advancedPanel,"Conciliar reserva incierta con este ID · sin POST",()=>Run(ReconcileAsync));
        retry=AddButton(advancedPanel,"Reintentar rechazo corregido · puede generar cargo",()=>Run(token=>GenerateAsync(token,true)));

        var family=Section(content,"03   Preparación BIM · sin nueva generación pagada");
        Field(family,"Nombre de la nueva familia (la simulación añade SIM_)",name);
        Field(family,"Descripción (opcional)",description);
        Field(family,"Altura física real confirmada (mm)",height);
        Field(family,"Eje vertical ORIGINAL del archivo OBJ",axis);
        Label(family,"Revit utiliza Z como eje vertical de destino. Algunos OBJ de Meshy pueden usar Y; compruebe su modelo. Si quedó acostado, cambie aquí el eje y cree otro RFA desde el mismo OBJ, sin generar otra malla ni consumir créditos.");
        Field(family,"Archivo OBJ disponible",objPath);
        AddButton(family,"Seleccionar OBJ local ya existente · sin Meshy",()=>Run(SelectLocalObjAsync,false));
        Field(family,"Modalidad de la nueva familia",familyMode);
        analyzeComponents=AddButton(family,"Analizar componentes del OBJ",()=>Run(AnalyzeComponentsAsync,false));
        family.Children.Add(componentInfo);
        configureColors=AddButton(family,"Configurar colores por componente",ConfigureComponentColors);
        editRegions=AddButton(family,"Abrir editor visual de regiones",()=>Run(EditRegionsAsync,false));
        Label(family,"Cada componente es una malla desconectada, no una pieza reconocida como asiento o pata. El modo por componentes usa una transformación global y no crea otra tarea IA.");
        family.Children.Add(editMaterials);controls.Add(editMaterials);
        Label(family,"Al marcar, podrá conservar la apariencia original, aplicar RGB uniforme, copiar un color gráfico o editar materiales independientes si el OBJ los expone. Afecta al nuevo RFA en Sombreado; Realista puede mostrar otra apariencia. No separa asiento, respaldo y patas.");

        var output=Section(content,"04   Resultado · creación local de RFA");
        createRfa=AddButton(output,"Crear familia RFA desde el OBJ disponible",()=>Run(CreateRfaAsync,false),true,true);
        Label(output,"Confirme la altura en mm y el eje original. Crear o repetir el RFA reutiliza el OBJ y no inicia otra generación Meshy.");
        Field(output,"Último RFA creado",rfaPath);
        var locations=new WrapPanel();output.Children.Add(locations);
        openRfa=AddButton(locations,"Abrir RFA en Revit",()=>Run(OpenRfaAsync,false));
        objFolder=AddButton(locations,"Abrir carpeta OBJ",()=>OpenFolder(AvailableObjPath));
        rfaFolder=AddButton(locations,"Abrir carpeta RFA",()=>OpenFolder(lastRfa));

        var labeling=Section(content,"Identificación manual · RFA por componentes activo");
        Label(labeling,"Abra un RFA por componentes, seleccione visualmente una importación en Revit y vuelva aquí para consultar su identidad. Los nombres son manuales; no separan caras ni modifican la geometría. Los OBJ completos no tienen componentes etiquetables.");
        var labelActions=new WrapPanel();labeling.Children.Add(labelActions);
        consultLabel=AddButton(labelActions,"Consultar selección",()=>Run(token=>LabelActionAsync(new(ComponentLabelAction.Selected),token),false));
        listLabels=AddButton(labelActions,"Consultar etiquetas existentes",()=>Run(token=>LabelActionAsync(new(ComponentLabelAction.List),token),false));
        labeling.Children.Add(labelIdentity);
        Label(labeling,"Caras OBJ: no disponibles de forma fiable en un RFA ya guardado; el identificador técnico permanece estable.");
        Field(labeling,"Nombre descriptivo manual (1–120 caracteres)",labelName);
        var editActions=new WrapPanel();labeling.Children.Add(editActions);
        saveLabel=AddButton(editActions,"Asignar o cambiar nombre",()=>Run(token=>LabelActionAsync(
            new(ComponentLabelAction.Set,selectedLabel?.ElementUniqueId,selectedLabel?.ComponentId,labelName.Text),token),false));
        clearLabel=AddButton(editActions,"Quitar etiqueta manual",()=>Run(token=>LabelActionAsync(
            new(ComponentLabelAction.Clear,selectedLabel?.ElementUniqueId,selectedLabel?.ComponentId),token),false));
        Label(labeling,"Etiquetas del RFA activo (seleccione la geometría en Revit para comprobar su correspondencia):");
        labeling.Children.Add(existingLabels);

        height.TextChanged+=(_,_)=>UpdateControls();name.TextChanged+=(_,_)=>UpdateControls();polycount.TextChanged+=(_,_)=>UpdateControls();
        taskId.TextChanged+=(_,_)=>UpdateControls();
        history.SelectionChanged+=(_,_)=>
        {
            if(history.SelectedItem is HistoryItem item)
            {
                if(result is not null && result.TaskId!=item.Record.TaskId)ClearArtifacts(true);
                taskId.Text=item.Record.TaskId ?? "";
                SetStatus($"Registro: {item.Record.Submission}; estado: {item.Record.State}; error: {item.Record.LastError}; auditorías: {item.Record.Audit.Length}."+
                    (item.Record.RetryNotBefore is { } next && next>DateTimeOffset.UtcNow?$" Retry-After hasta {next.ToLocalTime():g}; espere y actualice el historial.":"")+
                    (item.Record.TaskId is null?" Una reserva no es un ID. No repita un envío incierto.":" Puede consultar o continuar por ID sin pagar otra generación."));
            }
            UpdateControls();
        };
        taskId.TextChanged+=(_,_)=>
        {
            if(!busy && result is not null && !string.Equals(taskId.Text.Trim(),result.TaskId,StringComparison.OrdinalIgnoreCase))
            {ClearArtifacts(true);UpdateControls();}
        };
        mode.SelectionChanged+=(_,_)=>ChangeMode();scenario.SelectionChanged+=(_,_)=> {if(!busy)ChangeMode();};
        familyMode.SelectionChanged+=(_,_)=>
        {
            if(familyMode.SelectedIndex==1 && componentReport is null && AvailableObjPath is not null && !busy)
                Run(AnalyzeComponentsAsync,false);
            else UpdateControls();
        };
        Closing+=OnClosing;Closed+=OnClosed;
        Loaded+=(_,_)=>Run(RefreshHistoryAsync);
        ChangeMode();
    }
    private static StackPanel Section(Panel parent,string title)
    {
        var panel=new StackPanel {Margin=new Thickness(14)};
        parent.Children.Add(new GroupBox {Header=title,Content=panel,Background=Brushes.White,BorderBrush=BorderColor(),BorderThickness=new Thickness(1),Padding=new Thickness(0),Margin=new Thickness(0,0,0,10),FontWeight=FontWeights.SemiBold});
        return panel;
    }
    private static Brush Accent()=>new SolidColorBrush(System.Windows.Media.Color.FromRgb(21,64,105));
    private static Brush BorderColor()=>new SolidColorBrush(System.Windows.Media.Color.FromRgb(210,218,226));
    private static void Label(Panel panel,string text)=>panel.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.Normal,Margin=new Thickness(0,5,0,5)});
    private void Field(Panel panel,string label,Control control) {Label(panel,label);control.FontWeight=FontWeights.Normal;control.MinHeight=28;panel.Children.Add(control);controls.Add(control);}
    private Button AddButton(Panel panel,string text,Action action,bool track=true,bool primary=false)
    {
        var button=new Button {Content=text,Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,5,7,3),MinHeight=33,FontWeight=primary?FontWeights.SemiBold:FontWeights.Normal,HorizontalAlignment=primary?HorizontalAlignment.Stretch:HorizontalAlignment.Left};
        if(primary){button.Background=Accent();button.Foreground=Brushes.White;button.BorderBrush=Accent();}
        button.Click+=(_,_)=>action();panel.Children.Add(button);if(track)controls.Add(button);return button;
    }
    private void ChangeMode()
    {
        if(busy || closed)return;
        session?.Dispose();store=new(IsSimulated?MeshyMode.Simulated:MeshyMode.Real);
        simulation=IsSimulated?MeshySimulation.Create(store,(SimulationScenario)scenario.SelectedIndex):null;
        session=new(simulation?.Client ?? MeshyClient.CreateReal(true,store),store);
        ClearArtifacts(true);taskId.Text="";history.ItemsSource=null;
        modeNotice.Text=IsSimulated ? "SIMULACIÓN — OBJ fijo de prueba, no generado por IA. Sin red Meshy ni créditos." : "MODO REAL — cada nueva generación requiere confirmación. Consultar/reutilizar no crea tareas.";
        modeNotice.Foreground=IsSimulated?Brushes.DarkGreen:Brushes.DarkRed;
        scenario.Visibility=IsSimulated?Visibility.Visible:Visibility.Collapsed;
        if(name.Text is "SIM_Familia_Foto" or "Familia_Foto")name.Text=IsSimulated?"SIM_Familia_Foto":"Familia_Foto";
        SetStatus(!IsSimulated && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MESHY_API_KEY"))
            ? "Falta MESHY_API_KEY. Configure su clave propia como variable de usuario de Windows y reinicie Revit. No la escriba en esta ventana ni en el chat."
            : IsSimulated?"Prueba lista. Seleccione una imagen o recupere un ID simulado.":"Modo real listo; no se ha enviado ninguna solicitud.");
        UpdateControls();
        // No remote calls on mode changes; only the separate local history is read.
        if(IsLoaded)Run(RefreshHistoryAsync);
    }

    private void Run(Func<CancellationToken,Task> action,bool network=true)
    {
        if(busy || closed)return;
        busy=true;operationCancellation=new();int version=++operationVersion;
        stop.IsEnabled=network;UpdateControls();
        running=RunCoreAsync(action,operationCancellation,version);
    }
    private async Task RunCoreAsync(Func<CancellationToken,Task> action,CancellationTokenSource cancellation,int version)
    {
        try {await action(cancellation.Token);}
        catch(OperationCanceledException)
        {SetStatus("Operación local detenida. No se eliminó la tarea ni el OBJ. Un envío iniciado puede haber consumido créditos; use su ID o concilie la reserva.");}
        catch(MeshyException ex)
        {
            if(ex.TaskId is not null)taskId.Text=ex.TaskId;
            SetStatus(ex.Message+" ["+ex.Code+"]");
        }
        catch(ArgumentException ex) {SetStatus(ex.Message);}
        catch(IOException ex) {SetStatus("No se pudo preparar el archivo local: "+ex.Message+" El OBJ y cualquier ID previo se conservan.");}
        catch(Exception) {SetStatus("No se pudo completar la operación. Conserve el ID y el OBJ; puede repetir la conversión sin generar de nuevo.");}
        finally
        {
            if(!closed)
            {
                if(session.LastTaskId is { } id && string.IsNullOrWhiteSpace(taskId.Text))taskId.Text=id;
                try {await RefreshHistoryAsync(CancellationToken.None);}catch(MeshyException) {SetStatus(status.Text+" El historial local no pudo leerse; conserve sus archivos y no borre el diario.");}
                busy=false;stop.IsEnabled=false;UpdateControls();
            }
            cancellation.Dispose();if(version==operationVersion)operationCancellation=null;
        }
    }
    private async Task RefreshHistoryAsync(CancellationToken token)
    {
        var records=await session.HistoryAsync(token);
        if(closed)return;
        history.ItemsSource=records.OrderByDescending(r=>r.AttemptedAt ?? r.CreatedAt).Select(r=>new HistoryItem(r)).ToArray();
    }
    private async Task SelectPhotoAsync(CancellationToken token)
    {
        var dialog=new OpenFileDialog {Title="Seleccionar fotografía local",Filter="Fotografía (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true)return;
        photoPath=null;ClearArtifacts(true);preview.Source=null;
        var path=dialog.FileName;
        photoName.Text="Validando "+Path.GetFileName(path)+"…";previewNotice.Text="";
        var image=await Task.Run(()=>LocalImage.ReadAsync(path,token),token);
        BitmapSource? bitmap=null;
        try
        {
            bitmap=await Task.Run(()=>
            {
                using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                var value=new BitmapImage();value.BeginInit();value.CacheOption=BitmapCacheOption.OnLoad;value.DecodePixelWidth=240;value.StreamSource=stream;value.EndInit();value.Freeze();return value;
            },token);
        }
        catch(Exception ex) when(ex is NotSupportedException or FileFormatException or IOException or ArgumentException or System.Runtime.InteropServices.COMException)
        {previewNotice.Text="Vista previa no disponible con el codec de Windows; la imagen pasó la inspección estructural.";}
        token.ThrowIfCancellationRequested();if(closed)return;
        photoPath=path;photoName.Text=$"{Path.GetFileName(path)} · {image.Width} × {image.Height} · {new FileInfo(path).Length:N0} bytes";
        preview.Source=bitmap;if(bitmap is not null)previewNotice.Text="";
        SetStatus("Fotografía validada localmente. Aún no se ha enviado a Meshy.");
    }
    private MeshyOptions Options()
    {
        if(!int.TryParse(polycount.Text,out int count))throw new ArgumentException("Indique un número entero de polígonos.");
        var options=new MeshyOptions(count);options.Validate();return options;
    }
    private async Task GenerateAsync(CancellationToken token)=>await GenerateAsync(token,false);
    private async Task GenerateAsync(CancellationToken token,bool rejectedRetry)
    {
        if(photoPath is null)throw new ArgumentException("Seleccione y valide una fotografía primero.");
        string? rejectedFingerprint=null;
        if(rejectedRetry)
        {
            if(history.SelectedItem is not HistoryItem {Record.Submission:SubmissionState.Rejected} selected)
                throw new ArgumentException("Seleccione el rechazo que desea reintentar.");
            rejectedFingerprint=selected.Record.Fingerprint;
        }
        if(rejectedRetry && !ConfirmCorrection())return;
        result=null;lastRfa=null;objPath.Text="";rfaPath.Text="";progressBar.Value=0;taskId.Text="";
        SetStatus(IsSimulated?"Generando una simulación con el OBJ fijo de prueba…":"Comprobando fotografía, configuración y tareas existentes…");
        var generated=await session.GenerateAsync(photoPath,Options(),ConfirmPaidAsync,Progress(),rejectedRetry,token,rejectedFingerprint);
        ShowResult(generated);
    }
    private IProgress<GenerationProgress> Progress()
    {
        int version=operationVersion;
        return new Progress<GenerationProgress>(p=>
        {
            if(closed || version!=operationVersion)return;
            taskId.Text=p.TaskId;progressBar.Value=p.Percent ?? 0;
            SetStatus((IsSimulated?"SIMULACIÓN · ":"Meshy · ")+p.State+" · "+(p.Percent?.ToString() ?? "?")+" %");
        });
    }
    private Task<PaidGenerationApproval?> ConfirmPaidAsync(LocalImage image,MeshyOptions options)
        => Dispatcher.InvokeAsync(()=>
        {
            if(closed || operationCancellation?.IsCancellationRequested!=false)return null;
            bool accepted=Confirm("Autorizar una generación pagada",$"Proveedor: Meshy\nModelo: Meshy T2 / smart-topology\nPolígonos: {options.TargetPolycount} · sin texturas · OBJ\nCosto estimado: 5 créditos API (verificado 16/11/2026; puede cambiar).\n\nFotografía: {Path.GetFileName(photoPath)}\nSHA256: {image.Sha256}\n\nSe enviará esta fotografía a Meshy. El proveedor puede cobrar la solicitud; la calidad del modelo no está garantizada.\nLos créditos API son distintos de los de la aplicación web.\n\nSolo autorice si revisó la tarifa y desea iniciar UNA generación.","Autorizar UNA generación (≈5 créditos API)");
            return accepted?PaidGenerationApproval.ConfirmNewPaidGeneration(image.Sha256,options):null;
        }).Task;
    private bool ConfirmCorrection()=>Confirm("Reintento tras rechazo confirmado","Confirme que corrigió el motivo del rechazo (credencial, créditos, permisos o solicitud).\n\nSe conservará una auditoría del intento anterior. En modo real se pedirá una NUEVA autorización de costo. Esto no permite repetir una creación incierta.","Confirmo la corrección del motivo");
    private bool Confirm(string title,string text,string accept)
    {
        bool accepted=false;
        var dialog=new Window {Title=title,Owner=this,Width=Math.Min(560,SystemParameters.WorkArea.Width*0.9),Height=Math.Min(500,SystemParameters.WorkArea.Height*0.85),MinHeight=300,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.CanResize};
        confirmation=dialog;var root=new DockPanel {Margin=new Thickness(18)};dialog.Content=root;
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var cancel=new Button {Content="Cancelar",IsCancel=true,IsDefault=true,Padding=new Thickness(8),Margin=new Thickness(0,8,0,4)};
        cancel.Click+=(_,_)=>dialog.Close();footer.Children.Add(cancel);
        var allow=new Button {Content=accept,Padding=new Thickness(8),Margin=new Thickness(0,4,0,0)};
        allow.Click+=(_,_)=> {accepted=true;dialog.Close();};footer.Children.Add(allow);
        root.Children.Add(new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap}});
        dialog.Loaded+=(_,_)=>cancel.Focus();dialog.ShowDialog();confirmation=null;return accepted;
    }
    private string RequireId()
    {
        if(!Guid.TryParseExact(taskId.Text.Trim(),"D",out var id))throw new ArgumentException("Escriba o seleccione un ID de tarea válido (UUID).");
        return id.ToString();
    }
    private async Task QueryAsync(CancellationToken token)
    {
        var value=await session.QueryAsync(RequireId(),token);progressBar.Value=value.Progress.Percent ?? 0;
        SetStatus($"{(IsSimulated?"SIMULACIÓN":"Meshy")} · {value.Progress.State} · {value.Progress.Percent} % · error: {value.Progress.ErrorCode ?? "ninguno"}. Consultar no inicia generación.");
    }
    private async Task ResumeAsync(CancellationToken token)
    {result=null;lastRfa=null;objPath.Text="";rfaPath.Text="";ShowResult(await session.ResumeAsync(RequireId(),Progress(),token));}
    private void ShowResult(GeneratedObjResult generated)
    {
        if(closed)return;
        localObjPath=null;componentReport=null;componentColors=[];regionPlan=null;
        componentInfo.Text="Pulse Analizar componentes para conocer las piezas topológicas del OBJ.";
        result=generated;taskId.Text=generated.TaskId;objPath.Text=generated.LocalPath;progressBar.Value=100;
        SetStatus(IsSimulated?"SIMULACIÓN terminada. OBJ fijo de prueba conservado. Confirme altura para crear el RFA; no fue generado por IA.":"OBJ validado y conservado. Confirme una altura real y el eje; puede crear o repetir el RFA sin otra generación.");
    }
    private void ClearArtifacts(bool resetHeight)
    {
        result=null;localObjPath=null;componentReport=null;componentColors=[];regionPlan=null;lastRfa=null;objPath.Text="";rfaPath.Text="";
        componentInfo.Text="Seleccione un OBJ y pulse Analizar componentes.";
        if(resetHeight)height.Text="";
        if(!closed)UpdateControls();
    }

    private async Task SelectLocalObjAsync(CancellationToken token)
    {
        var picker=new OpenFileDialog {Title="Seleccione un OBJ local ya existente",Filter="Modelo OBJ (*.obj)|*.obj",CheckFileExists=true};
        if(picker.ShowDialog(this)!=true)return;
        string path=picker.FileName;
        await Task.Run(()=>ObjInspection.Inspect(path),token);
        token.ThrowIfCancellationRequested();
        result=null;localObjPath=path;lastRfa=null;componentReport=null;componentColors=[];regionPlan=null;
        taskId.Text="";objPath.Text=path;rfaPath.Text="";
        componentInfo.Text="Pulse Analizar componentes para conocer las piezas topológicas del OBJ.";
        SetStatus("OBJ local validado. No se consultó ni generó una tarea Meshy.");
        UpdateControls();
    }

    private async Task AnalyzeComponentsAsync(CancellationToken token)
    {
        string path=AvailableObjPath ?? throw new ArgumentException("Seleccione o recupere un OBJ primero.");
        var report=await Task.Run(()=>ObjStructure.Analyze(path),token);
        token.ThrowIfCancellationRequested();
        componentReport=report;componentColors=[];
        componentInfo.Text=report.Components.Count==1
            ? "Una sola componente topológica. Puede importarla completa o asignar materiales por regiones."
            : $"{report.Components.Count} componentes topológicos detectados. Son mallas desconectadas por índices; no se identifican automáticamente asiento, respaldo o patas.";
        SetStatus(componentInfo.Text);
        UpdateControls();
    }

    private void ConfigureComponentColors()
    {
        if(componentReport is null || componentReport.Components.Count<2)
        {SetStatus("Analice primero un OBJ con al menos dos componentes.");return;}
        var dialog=new ComponentColorDialog(componentReport.Components,componentColors) {Owner=this};
        if(dialog.ShowDialog()==true)
        {
            componentColors=dialog.SelectedColors;
            SetStatus($"{componentColors.Count} componente(s) recibirán RGB propio; los demás conservarán su apariencia.");
        }
    }
    private async Task ReconcileAsync(CancellationToken token)
    {
        if(history.SelectedItem is not HistoryItem item || item.Record.Submission!=SubmissionState.Uncertain || item.Record.TaskId is not null)
            throw new ArgumentException("Seleccione una reserva incierta sin ID en el historial.");
        string id=RequireId();
        if(!Confirm("Conciliar una reserva sin crear otra tarea",$"Reserva: {item.Record.Fingerprint}\nIntento: {item.Record.AttemptedAt}\nID recuperado: {id}\n\nCompruebe en Meshy que este ID corresponde exactamente a la fotografía, perfil y momento del intento. Consultar por ID solo demuestra que la tarea existe, no su asociación con esta imagen.\n\nConfirme únicamente si tiene esa evidencia. Quedará una auditoría local; no se enviará POST.","Confirmo la evidencia y la asociación de este ID"))return;
        var record=await session.ReconcileAsync(item.Record.Fingerprint,id,true,token);taskId.Text=record.TaskId;
        SetStatus("ID conciliado con auditoría. Use Continuar / descargar OBJ; no se creó otra generación.");
    }
    private async Task CreateRfaAsync(CancellationToken token)
    {
        string source=AvailableObjPath ?? throw new ArgumentException("Seleccione o recupere un OBJ válido primero.");
        if(!double.TryParse(height.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double mm) &&
            !double.TryParse(height.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out mm))throw new ArgumentException("Confirme una altura física real en milímetros.");
        string familyName=name.Text,details=description.Text;var vertical=(ObjVerticalAxis)axis.SelectedIndex;bool simulated=IsSimulated;
        bool local=localObjPath is not null;
        var generated=result;
        var request=await Task.Run(()=>local
            ? new ObjImportRequest(source,ObjUnit.Millimeters,vertical,mm,familyName,"OBJ local; dimensiones informativas. "+details,true)
            : PhotoFamilyRequest.Create(generated!,mm,vertical,familyName,details,simulated),token);
        ObjAssemblyRequest? assembly=null;
        if(familyMode.SelectedIndex!=0)
        {
            var report=componentReport ?? await Task.Run(()=>ObjStructure.Analyze(source),token);
            componentReport=report;
            var plan=familyMode.SelectedIndex==2
                ? regionPlan ?? throw new ArgumentException("Confirme un plan en el editor visual primero.")
                : null;
            if(report.Components.Count<2 && plan is null)throw new ArgumentException("El OBJ tiene una sola componente; elija OBJ completo.");
            int limit=plan is null ? ObjAssemblyImporter.MaxAssemblyComponents : ObjComponentExtractor.MaxRegionComponents;
            if(report.Components.Count>limit)throw new ArgumentException($"Más de {limit} componentes: revise el OBJ antes de importar.");
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BIM Family AI","Components");
            Directory.CreateDirectory(root);
            string folder=Path.Combine(root,"extraction-"+Guid.NewGuid().ToString("N"));
            SetStatus("Extrayendo y validando piezas locales; no se llamará a Meshy.");
            var colors=componentColors.Where(c=>plan is null || !plan.Components.Any(p=>p.ComponentId==$"component_{c.Number:000}")).ToArray();
            assembly=await Task.Run(()=>
            {
                var extraction=ObjComponentExtractor.Extract(source,folder);
                token.ThrowIfCancellationRequested();
                string? regions=plan is null ? null : ObjComponentExtractor.DeriveRegions(source,
                    extraction.OutputDirectory,plan,folder+"-regions").OutputDirectory;
                return new ObjAssemblyRequest(extraction.OutputDirectory,colors,regions);
            },token);
        }
        token.ThrowIfCancellationRequested();SetStatus("Solicitud encolada para Revit. Termine cualquier comando activo. La importación nativa puede ocupar temporalmente la interfaz.");
        var outcome=await bridge.Enqueue(new(request,null,assembly is null && editMaterials.IsChecked==true,assembly));
        if(outcome.OutputPath is not null) {lastRfa=outcome.OutputPath;rfaPath.Text=lastRfa;}
        SetStatus(outcome.Status==Autodesk.Revit.UI.Result.Succeeded ? "RFA creado y verificado tras reabrir. "+(assembly is null?"OBJ completo.":$"{componentReport!.Components.Count} componentes ensamblados.")+" "+outcome.Message : "No se creó el RFA: "+outcome.Message+" El OBJ sigue disponible; corrija los datos y repita solo Crear familia RFA.");
    }
    private async Task EditRegionsAsync(CancellationToken token)
    {
        string source=AvailableObjPath ?? throw new ArgumentException("Seleccione o recupere un OBJ primero.");
        var vertical=(ObjVerticalAxis)axis.SelectedIndex;
        var initial=regionPlan;
        SetStatus("Preparando la vista previa de las caras originales…");
        var root=Path.Combine(Path.GetTempPath(),"BFAI-region-preview-"+Guid.NewGuid().ToString("N"));
        try
        {
            var prepared=await Task.Run(()=>
            {
                var extracted=ObjComponentExtractor.Extract(source,root);
                token.ThrowIfCancellationRequested();
                var plan=initial is null ? null : ObjComponentExtractor.ValidateRegionPlan(source,root,initial);
                return (extracted, ObjComponentExtractor.ReadRegionPreview(source,root,vertical), plan);
            },token);
            token.ThrowIfCancellationRequested();
            regionEditor=new FaceRegionEditor(source,prepared.extracted,prepared.Item2,prepared.plan) {Owner=this};
            if(regionEditor.ShowDialog()==true)
            {
                var confirmed=regionEditor.AcceptedPlan;
                regionPlan=confirmed?.Components.Count>0 ? confirmed : null;
                familyMode.SelectedIndex=regionPlan is null ? 0 : 2;
                SetStatus(regionPlan is null ? "Sin regiones activas." :
                    $"{regionPlan.Components.Sum(c=>c.Regions.Count)} regiones y {regionPlan.Components.Count} componentes con ajustes listos para crear el RFA.");
            }
            else SetStatus("Edición cancelada. Se conserva el último plan confirmado.");
        }
        finally
        {
            if(regionEditor is not null) await regionEditor.PendingFileOperation;
            regionEditor=null;
            if(Directory.Exists(root)) Directory.Delete(root,true);
        }
    }
    private async Task OpenRfaAsync(CancellationToken token)
    {token.ThrowIfCancellationRequested();if(lastRfa is null)throw new ArgumentException("Cree un RFA primero.");var outcome=await bridge.Enqueue(new(null,lastRfa));SetStatus(outcome.Message);}
    private async Task LabelActionAsync(ComponentLabelRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var outcome=await bridge.Enqueue(new(null,null,LabelRequest:request));
        if(outcome.Status!=Autodesk.Revit.UI.Result.Succeeded || outcome.Labels is null)
        {
            if(request.Action is ComponentLabelAction.Selected or ComponentLabelAction.List)
            {selectedLabel=null;labelName.Text="";labelIdentity.Text="No se identificó ninguna pieza; consulte una importación válida del RFA activo.";UpdateControls();}
            SetStatus(outcome.Message);return;
        }
        selectedLabel=outcome.Labels.Selected;
        labelName.Text=selectedLabel?.Name ?? "";
        labelIdentity.Text=selectedLabel is null
            ? $"{outcome.Labels.Items.Count} componentes BIM Family AI encontrados. Seleccione una pieza en Revit para identificarla visualmente."
            : $"Seleccionado: {selectedLabel.ComponentId} · "+(selectedLabel.Name is null?"sin etiqueta manual":$"nombre manual: {selectedLabel.Name}");
        existingLabels.ItemsSource=outcome.Labels.Items.Select(i=>i.ComponentId+"  →  "+(i.Name ?? "(sin etiqueta)")).ToArray();
        SetStatus(request.Action switch
        {
            ComponentLabelAction.Set=>"Etiqueta guardada en la familia activa. Use Guardar de Revit para conservarla en el archivo RFA.",
            ComponentLabelAction.Clear=>"Etiqueta manual retirada de la familia activa. Use Guardar de Revit para conservar el cambio.",
            _=>"Identidades consultadas en el RFA activo. Los nombres son asignados manualmente."
        });
        UpdateControls();
    }
    private void OpenFolder(string? path)
    {
        try {if(path is null || !File.Exists(path))throw new IOException();Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) {UseShellExecute=true});}
        catch(Exception) {SetStatus("No se pudo abrir la carpeta del resultado local.");}
    }
    private void UpdateControls()
    {
        foreach(var control in controls)control.IsEnabled=!busy;
        bool validPhoto=photoPath is not null && File.Exists(photoPath);
        bool validOptions=int.TryParse(polycount.Text,out int count) && count>=100 && count<=15000;
        bool validHeight=(double.TryParse(height.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double mm) ||
            double.TryParse(height.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out mm)) && double.IsFinite(mm) && mm>0;
        bool validName=true;
        try {ObjInspection.ValidateName(name.Text.Trim());}catch(ArgumentException) {validName=false;}
        bool validId=Guid.TryParseExact(taskId.Text.Trim(),"D",out _);
        generate.IsEnabled=!busy && validPhoto && validOptions && (IsSimulated || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MESHY_API_KEY")));
        query.IsEnabled=!busy && validId;
        resume.IsEnabled=!busy && validId;
        bool hasObj=AvailableObjPath is { } path && File.Exists(path);
        createRfa.IsEnabled=!busy && hasObj && validHeight && validName &&
            (familyMode.SelectedIndex switch {0=>true,1=>componentReport?.Components.Count!=1,_=>regionPlan is not null});
        analyzeComponents.IsEnabled=!busy && hasObj;
        editRegions.IsEnabled=!busy && hasObj;
        configureColors.IsEnabled=!busy && familyMode.SelectedIndex==1 && componentReport?.Components.Count>=2;
        consultLabel.IsEnabled=!busy;
        listLabels.IsEnabled=!busy;
        saveLabel.IsEnabled=!busy && selectedLabel is not null;
        clearLabel.IsEnabled=!busy && selectedLabel is not null;
        editMaterials.IsEnabled=!busy && familyMode.SelectedIndex==0;
        editMaterials.Visibility=familyMode.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
        openRfa.IsEnabled=!busy && lastRfa is not null && File.Exists(lastRfa);
        objFolder.IsEnabled=!busy && AvailableObjPath is { } available && File.Exists(available);
        rfaFolder.IsEnabled=!busy && lastRfa is not null && File.Exists(lastRfa);
        reconcile.IsEnabled=!busy && validId && history.SelectedItem is HistoryItem {Record.Submission:SubmissionState.Uncertain,Record.TaskId:null};
        retry.IsEnabled=!busy && history.SelectedItem is HistoryItem {Record.Submission:SubmissionState.Rejected} item &&
            validPhoto && validOptions && (IsSimulated || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MESHY_API_KEY"))) &&
            (item.Record.RetryNotBefore is null || item.Record.RetryNotBefore<=DateTimeOffset.UtcNow);
        phase.Text=lastRfa is not null && File.Exists(lastRfa)?"● Fase 4 de 4 · RFA generado":
            hasObj? validHeight && validName?"● Fase 3 de 4 · OBJ listo; datos BIM completos":"● Fase 3 de 4 · OBJ listo; complete nombre y altura en mm":
            busy?"● Fase 2 de 4 · operación en curso":
            validPhoto?"● Fase 1 de 4 · fotografía validada; puede generar o recuperar un OBJ":"● Fase 1 de 4 · seleccione una fotografía o recupere una tarea existente";
        phase.Foreground=Accent();
        traffic.Text=simulation is null?"Modo real: consultar/reutilizar no autoriza una nueva generación.":$"Solo transporte simulado · POST: {simulation.CreationRequests} · GET estado: {simulation.StatusRequests} · descargas: {simulation.Downloads} · créditos: 0";
    }
    private void SetStatus(string text) {if(!closed)status.Text=text;}
    private void OnClosing(object? sender,CancelEventArgs args)
    {
        if(bridge.IsExecuting && !shutdown) {args.Cancel=true;SetStatus("Espere a que termine la importación nativa antes de cerrar.");return;}
        operationCancellation?.Cancel();
    }
    private async void OnClosed(object? sender,EventArgs args)
    {
        closed=true;operationCancellation?.Cancel();bridge.RequestClose();
        if(running is not null)try {await running;}catch(Exception) { }
        session.Dispose();preview.Source=null;
    }
    internal void CloseForShutdown() {shutdown=true;regionEditor?.Close();confirmation?.Close();Close();}
}
