using Autodesk.Revit.DB;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BimFamilyAI.Core;

namespace BimFamilyAI.Revit;

internal sealed class ImportedMaterialDialog : Window
{
    internal sealed record Choice(string Name,byte Red,byte Green,byte Blue);
    internal Choice? Selected {get;private set;}
    internal ImportedMaterialDialog(Document doc,string familyName)
    {
        Title="BIM Family AI — Materiales y apariencia";Width=Math.Min(560,SystemParameters.WorkArea.Width*0.9);
        Height=Math.Min(560,SystemParameters.WorkArea.Height*0.92);MinWidth=440;MinHeight=350;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var scroll=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Content=scroll;
        var panel=new StackPanel {Margin=new Thickness(20)};scroll.Content=panel;
        void Text(string value)=>panel.Children.Add(new TextBlock {Text=value,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,5)});
        panel.Children.Add(new TextBlock {Text="Apariencia del nuevo RFA",FontSize=18,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(System.Windows.Media.Color.FromRgb(21,64,105))});
        Text("Elija una opción. Cancelar no publica un RFA nuevo.");
        var mode=new ComboBox {ItemsSource=new[]{"Conservar apariencia original","Aplicar color RGB uniforme en Sombreado","Copiar RGB gráfico de un material disponible"},SelectedIndex=0,MinHeight=30};panel.Children.Add(mode);
        Text("El color gráfico persiste en Sombreado. Realista puede usar otra apariencia. Esta opción no crea piezas independientes ni altera la geometría o el OBJ original.");
        var regions=ImportedMaterials.Inspect(doc);
        Text($"Regiones detectadas: {regions.Count}. No se identifican automáticamente asiento, respaldo ni patas. Los materiales explícitos se editan solo si pertenecen a esta importación nueva.");
        Text("Material disponible en esta familia (solo se copia su RGB):");
        var available=new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().OrderBy(m=>m.Name).ToArray();
        var source=new ComboBox {ItemsSource=available.Select(m=>m.Name).ToArray(),SelectedIndex=available.Length>0?0:-1};panel.Children.Add(source);
        Text("Nombre identificable del material nuevo:");var name=new TextBox {Text=familyName+"_Color",MinHeight=28};panel.Children.Add(name);
        Text("RGB: rojo, verde y azul (0–255, separados por comas):");var rgb=new TextBox {Text="0,80,220",MinHeight=28};panel.Children.Add(rgb);
        var swatch=new Border {Width=32,Height=24,HorizontalAlignment=HorizontalAlignment.Left,BorderThickness=new Thickness(1),BorderBrush=Brushes.Gray,Margin=new Thickness(0,8,0,3)};panel.Children.Add(swatch);
        var swatchText=new TextBlock {TextWrapping=TextWrapping.Wrap};panel.Children.Add(swatchText);
        source.IsEnabled=false;name.IsEnabled=false;rgb.IsEnabled=false;
        void UpdatePreview()
        {
            source.IsEnabled=mode.SelectedIndex==2;rgb.IsEnabled=mode.SelectedIndex==1;name.IsEnabled=mode.SelectedIndex!=0;
            byte r,g,b;
            if(mode.SelectedIndex==0){swatch.Visibility=System.Windows.Visibility.Collapsed;swatchText.Text="Se conservará la apariencia original.";return;}
            swatch.Visibility=System.Windows.Visibility.Visible;
            if(mode.SelectedIndex==2 && source.SelectedIndex>=0)
            {var c=available[source.SelectedIndex].Color;r=c.Red;g=c.Green;b=c.Blue;}
            else if(mode.SelectedIndex==1 && TryRgb(rgb.Text,out r,out g,out b)) { }
            else {swatch.Background=Brushes.Transparent;swatchText.Text="Seleccione un material o escriba RGB válido.";return;}
            swatch.Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(r,g,b));swatchText.Text=$"RGB {r}, {g}, {b} · color gráfico";
        }
        mode.SelectionChanged+=(_,_)=>UpdatePreview();source.SelectionChanged+=(_,_)=>UpdatePreview();rgb.TextChanged+=(_,_)=>UpdatePreview();UpdatePreview();
        var error=new TextBlock {TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DarkRed};panel.Children.Add(error);
        var accept=new Button {Content="Continuar y guardar nuevo RFA",Margin=new Thickness(0,12,0,5),Padding=new Thickness(10,7,10,7),MinHeight=34,Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(21,64,105)),Foreground=Brushes.White};panel.Children.Add(accept);
        accept.Click+=(_,_)=>{try{
            if(mode.SelectedIndex!=0){ObjInspection.ValidateName(name.Text.Trim());byte r,g,b;
                if(mode.SelectedIndex==2){if(source.SelectedIndex<0)throw new ArgumentException("Seleccione un material.");var c=available[source.SelectedIndex].Color;r=c.Red;g=c.Green;b=c.Blue;}
                else if(!TryRgb(rgb.Text,out r,out g,out b))throw new ArgumentException("Indique RGB válido, por ejemplo 0,80,220.");
                Selected=new(name.Text.Trim(),r,g,b);
            }DialogResult=true;
        }catch(Exception ex){error.Text=ex.Message;}};
        var cancel=new Button {Content="Cancelar creación de RFA",IsCancel=true,Padding=new Thickness(8)};panel.Children.Add(cancel);
    }
    private static bool TryRgb(string value,out byte r,out byte g,out byte b)
    {
        r=g=b=0;var parts=value.Split(',');
        return parts.Length==3 && byte.TryParse(parts[0].Trim(),out r) && byte.TryParse(parts[1].Trim(),out g) && byte.TryParse(parts[2].Trim(),out b);
    }
}
